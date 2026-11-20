using System.Globalization;

namespace VentasReportesApi;

// =====================================================================================
//  Monitor de tiendas: revisa cada pocos minutos que el SQL Server de cada tienda activa
//  responda. Si una tienda deja de responder (N revisiones seguidas) avisa en el canal de
//  esa tienda del chat; cuando vuelve, avisa que se recuperó y cuánto tiempo estuvo caída.
//  El estado se guarda en la base (tabla MonitorEstado) para no repetir avisos al reiniciar.
// =====================================================================================

public sealed record EstadoMonitor(string Tienda, bool Caida, int Fallos, string? Desde, string? Mensaje);

public sealed partial class Almacen
{
    public void InicializarMonitor()
    {
        Ejecutar(@"CREATE TABLE IF NOT EXISTS MonitorEstado (
                       Tienda  TEXT PRIMARY KEY COLLATE NOCASE,
                       Caida   INTEGER NOT NULL DEFAULT 0,
                       Fallos  INTEGER NOT NULL DEFAULT 0,
                       Desde   TEXT NULL,
                       Mensaje TEXT NULL)");
    }

    public Dictionary<string, EstadoMonitor> LeerMonitor()
    {
        var d = new Dictionary<string, EstadoMonitor>(StringComparer.CurrentCultureIgnoreCase);
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Tienda, Caida, Fallos, IFNULL(Desde,''), IFNULL(Mensaje,'') FROM MonitorEstado";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string desde = r.GetString(3), msg = r.GetString(4);
            d[r.GetString(0)] = new EstadoMonitor(r.GetString(0), r.GetInt64(1) == 1, (int)r.GetInt64(2),
                                                  desde.Length == 0 ? null : desde, msg.Length == 0 ? null : msg);
        }
        return d;
    }

    public void GuardarMonitor(EstadoMonitor e) =>
        Ejecutar(@"INSERT INTO MonitorEstado (Tienda, Caida, Fallos, Desde, Mensaje) VALUES ($t, $c, $f, $d, $m)
                   ON CONFLICT(Tienda) DO UPDATE SET Caida = excluded.Caida, Fallos = excluded.Fallos,
                                                     Desde = excluded.Desde, Mensaje = excluded.Mensaje",
                 ("$t", e.Tienda), ("$c", e.Caida ? 1 : 0), ("$f", e.Fallos), ("$d", e.Desde), ("$m", e.Mensaje));
}

public static class MonitorTiendas
{
    public static void Iniciar(WebApplication app, Almacen db, IConfiguration cfg,
                               Func<Tienda, CancellationToken, Task<(bool ok, long ms, string msg)>> probar)
    {
        var c = cfg.GetSection("Monitor");
        if (!c.GetValue("Activo", true)) { app.Logger.LogInformation("Monitor de tiendas desactivado (Monitor:Activo = false)."); return; }
        int minutos = Math.Clamp(c.GetValue("MinutosEntreRevisiones", 2), 1, 60);
        int fallosAlerta = Math.Clamp(c.GetValue("FallosParaAlerta", 2), 1, 10);
        int horaInicio = Math.Clamp(c.GetValue("HoraInicio", 7), 0, 23);
        int horaFin = Math.Clamp(c.GetValue("HoraFin", 22), 1, 24);

        db.InicializarMonitor();
        var ct = app.Lifetime.ApplicationStopping;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch { return; }    // que la API termine de arrancar
            app.Logger.LogInformation("Monitor de tiendas: cada {M} min, alerta tras {F} fallos, de {I}:00 a {H}:00.",
                                      minutos, fallosAlerta, horaInicio, horaFin);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int h = DateTime.Now.Hour;
                    if (h >= horaInicio && h < horaFin) await RevisarAsync(app, db, probar, fallosAlerta, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { app.Logger.LogWarning(ex, "Monitor de tiendas: error en la revisión"); }
                try { await Task.Delay(TimeSpan.FromMinutes(minutos), ct); } catch { return; }
            }
        });
    }

    private static async Task RevisarAsync(WebApplication app, Almacen db,
                                           Func<Tienda, CancellationToken, Task<(bool ok, long ms, string msg)>> probar,
                                           int fallosAlerta, CancellationToken ct)
    {
        var tiendas = db.ListarTiendas().Where(t => t.Activa).ToList();
        var estados = db.LeerMonitor();
        var resultados = await Task.WhenAll(tiendas.Select(async t => (t, r: await probar(t, ct))));

        foreach (var (t, r) in resultados)
        {
            estados.TryGetValue(t.Nombre, out var antes);
            antes ??= new EstadoMonitor(t.Nombre, false, 0, null, null);
            string ahora = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            if (r.ok)
            {
                if (antes.Caida)
                {
                    string duracion = Duracion(antes.Desde);
                    Publicar(app, t.Nombre, $"🟢 {t.Nombre} volvió a responder a las {DateTime.Now:HH:mm}" +
                                            (duracion.Length > 0 ? $" (estuvo sin conexión {duracion})." : "."));
                    app.Logger.LogInformation("Monitor: {T} se recuperó", t.Nombre);
                }
                if (antes.Caida || antes.Fallos > 0) db.GuardarMonitor(new EstadoMonitor(t.Nombre, false, 0, null, null));
                continue;
            }

            int fallos = antes.Fallos + 1;
            string? desde = antes.Desde ?? ahora;                     // momento del primer fallo
            bool caida = antes.Caida;
            if (!caida && fallos >= fallosAlerta)
            {
                caida = true;
                var d = DateTime.ParseExact(desde, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                Publicar(app, t.Nombre, $"🔴 {t.Nombre} no responde desde las {d:HH:mm}. " +
                                        $"Los reportes de esta tienda no van a salir hasta que vuelva.\nDetalle: {r.msg}");
                app.Logger.LogWarning("Monitor: {T} no responde ({Msg})", t.Nombre, r.msg);
            }
            db.GuardarMonitor(new EstadoMonitor(t.Nombre, caida, fallos, desde, r.msg));
        }
    }

    private static void Publicar(WebApplication app, string tienda, string texto)
    {
        try { ChatApi.PublicarSistema?.Invoke("tienda:" + tienda, texto); }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Monitor: no se pudo publicar en el chat"); }
    }

    private static string Duracion(string? desde)
    {
        if (desde == null || !DateTime.TryParseExact(desde, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return "";
        var t = DateTime.Now - d;
        if (t.TotalMinutes < 1) return "menos de un minuto";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        return $"{(int)t.TotalDays} días {t.Hours} h";
    }
}
