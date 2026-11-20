using System.Collections.Concurrent;

namespace VentasReportesApi;

// =====================================================================================
//  Concurrencia y carga sobre los POS de las tiendas
//
//  1) Límites por niveles (en este orden, siempre el mismo para no trabarse):
//       por usuario  → que una persona no acapare el servidor
//       por tienda   → que una tienda lenta no frene a las demás ni se sature su SQL Server
//       global       → tope total de consultas abiertas desde la API
//     Si no hay turno en 'espera' (2 min), se responde 503 "servidor ocupado" en vez de quedar colgado.
//
//  2) Consultas compartidas + caché corta:
//       si la misma consulta (reporte, tienda, fechas y filtros) ya está corriendo, los demás
//       esperan ESE resultado; al terminar se guarda unos minutos (menos si incluye hoy).
//       Si todos los que esperaban cancelan, la consulta se corta en el SQL Server.
//
//  3) Lectura sin bloqueo (READ UNCOMMITTED): los reportes no frenan a los cajeros.
// =====================================================================================

/// <summary>No hubo turno a tiempo: el servidor está atendiendo demasiadas consultas.</summary>
public sealed class ServidorOcupadoException : Exception
{
    public ServidorOcupadoException() : base("Servidor ocupado") { }
}

public static class Lectura
{
    /// <summary>true = los reportes leen sin bloquear a las cajas (puede incluir una venta que se está grabando en ese instante).</summary>
    public static bool SinBloqueo { get; set; } = true;
    public static string Prefijo => SinBloqueo ? "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n" : "";
}

public sealed class Concurrencia
{
    private readonly SemaphoreSlim global;
    private readonly int porTienda, porUsuario;
    private readonly TimeSpan espera;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> tiendas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> usuarios = new(StringComparer.OrdinalIgnoreCase);
    private int activas, enCola;

    public Concurrencia(int global, int porTienda, int porUsuario, TimeSpan espera)
    {
        this.global = new SemaphoreSlim(Math.Max(1, global));
        this.porTienda = Math.Max(1, porTienda);
        this.porUsuario = Math.Max(1, porUsuario);
        this.espera = espera;
    }

    public int Activas => Volatile.Read(ref activas);
    public int EnCola => Volatile.Read(ref enCola);

    /// <summary>Espera turno (usuario → tienda → global). null = no hubo turno a tiempo.</summary>
    public async Task<IDisposable?> EntrarAsync(string tienda, string usuario, CancellationToken ct)
    {
        var u = usuarios.GetOrAdd(usuario, _ => new SemaphoreSlim(porUsuario));
        var t = tiendas.GetOrAdd(tienda, _ => new SemaphoreSlim(porTienda));
        var limite = DateTime.UtcNow + espera;
        TimeSpan Resta() { var r = limite - DateTime.UtcNow; return r > TimeSpan.Zero ? r : TimeSpan.Zero; }

        var tomados = new List<SemaphoreSlim>(3);
        Interlocked.Increment(ref enCola);
        try
        {
            foreach (var s in new[] { u, t, global })
            {
                if (!await s.WaitAsync(Resta(), ct)) { foreach (var x in tomados) x.Release(); return null; }
                tomados.Add(s);
            }
        }
        catch
        {
            foreach (var x in tomados) x.Release();
            throw;
        }
        finally { Interlocked.Decrement(ref enCola); }

        Interlocked.Increment(ref activas);
        return new Turno(() =>
        {
            Interlocked.Decrement(ref activas);
            for (int i = tomados.Count - 1; i >= 0; i--) tomados[i].Release();
        });
    }

    private sealed class Turno : IDisposable
    {
        private Action? liberar;
        public Turno(Action liberar) { this.liberar = liberar; }
        public void Dispose() => Interlocked.Exchange(ref liberar, null)?.Invoke();
    }
}

public enum OrigenResultado { Consulta, Compartido, Cache }

/// <summary>Une consultas idénticas en curso y guarda el resultado unos minutos.</summary>
public sealed class Compartidos
{
    private sealed class Vuelo
    {
        public readonly CancellationTokenSource Cts = new(TimeSpan.FromMinutes(7));
        public Task<object>? Tarea;
        public int Esperando;
    }

    private sealed record Guardado(object Valor, DateTime Vence, long Peso, DateTime Creado);

    private readonly ConcurrentDictionary<string, Lazy<Vuelo>> enCurso = new();
    private readonly ConcurrentDictionary<string, Guardado> cache = new();
    private readonly long pesoMaximo;
    private long pesoActual;

    /// <param name="pesoMaximo">Tope de filas guardadas en memoria entre todos los resultados.</param>
    public Compartidos(long pesoMaximo) { this.pesoMaximo = pesoMaximo; }

    public int EnCache => cache.Count;

    public async Task<(T valor, OrigenResultado origen)> ObtenerAsync<T>(string clave, TimeSpan vida,
        Func<CancellationToken, Task<T>> consulta, Func<T, long> peso, long pesoMaximoUno, CancellationToken ct) where T : class
    {
        if (cache.TryGetValue(clave, out var g))
        {
            if (g.Vence > DateTime.UtcNow) return ((T)g.Valor, OrigenResultado.Cache);
            Quitar(clave);
        }

        bool soyPrimero = false;
        var lazy = enCurso.GetOrAdd(clave, _ => new Lazy<Vuelo>(() =>
        {
            soyPrimero = true;
            var v = new Vuelo();
            v.Tarea = Ejecutar(clave, v, vida, consulta, peso, pesoMaximoUno);
            return v;
        }));
        var vuelo = lazy.Value;
        Interlocked.Increment(ref vuelo.Esperando);
        try
        {
            var r = await vuelo.Tarea!.WaitAsync(ct);
            return ((T)r, soyPrimero ? OrigenResultado.Consulta : OrigenResultado.Compartido);
        }
        finally
        {
            // si ya nadie espera este resultado, se corta la consulta en la tienda
            if (Interlocked.Decrement(ref vuelo.Esperando) == 0 && !vuelo.Tarea!.IsCompleted)
                try { vuelo.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task<object> Ejecutar<T>(string clave, Vuelo v, TimeSpan vida, Func<CancellationToken, Task<T>> consulta,
                                           Func<T, long> peso, long pesoMaximoUno) where T : class
    {
        try
        {
            await Task.Yield();                                   // el que llega primero no ejecuta la consulta "dentro" del GetOrAdd
            var r = await consulta(v.Cts.Token);
            long p = Math.Max(1, peso(r));
            if (vida > TimeSpan.Zero && p <= pesoMaximoUno)
            {
                cache[clave] = new Guardado(r, DateTime.UtcNow + vida, p, DateTime.UtcNow);
                Interlocked.Add(ref pesoActual, p);
                Recortar();
            }
            return r;
        }
        finally
        {
            enCurso.TryRemove(clave, out _);
            v.Cts.Dispose();
        }
    }

    private void Quitar(string clave)
    {
        if (cache.TryRemove(clave, out var g)) Interlocked.Add(ref pesoActual, -g.Peso);
    }

    /// <summary>Saca lo vencido y, si hay demasiadas filas en memoria, lo más viejo.</summary>
    public void Recortar()
    {
        var ahora = DateTime.UtcNow;
        foreach (var (k, g) in cache) if (g.Vence <= ahora) Quitar(k);
        if (Interlocked.Read(ref pesoActual) <= pesoMaximo) return;
        foreach (var (k, _) in cache.OrderBy(x => x.Value.Creado))
        {
            Quitar(k);
            if (Interlocked.Read(ref pesoActual) <= pesoMaximo) break;
        }
    }
}
