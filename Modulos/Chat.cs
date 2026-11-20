using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace VentasReportesApi;

// =====================================================================================
//  Chat entre usuarios
//   - Canal "general": todos los usuarios.
//   - Canal "tienda:NOMBRE": usuarios con esa tienda asignada + ADMIN y JEFE.
//   - Canal "dm:login1|login2": mensaje privado entre dos usuarios.
//  Los mensajes se guardan en datos/seguridad.db (tablas ChatMensajes, ChatLeidos, ChatPrivados).
// =====================================================================================

public sealed record ChatMensaje(long Id, string Canal, string Login, string Nombre, string Texto, string Fecha,
                                 string? Adjunto, bool Eliminado);
public sealed record ChatAdjuntoPeticion(List<string>? Sucursales, DateTime? Desde, DateTime? Hasta);
public sealed record ChatEnvioPeticion(string? Canal, string? Texto, ChatAdjuntoPeticion? Adjunto, string? Archivo = null);
public sealed record ChatLeidoPeticion(string? Canal, long HastaId);
public sealed record ChatCanalPeticion(string? Canal);
/// <summary>Adjunto guardado con el mensaje: un reporte compartido (tiendas + fechas) o un archivo.</summary>
public sealed record ChatAdjuntoGuardado(List<string>? Sucursales, string? Desde, string? Hasta,
                                         string? ArchivoId = null, string? ArchivoNombre = null, long ArchivoTamano = 0,
                                         string? ArchivoTipo = null);
public sealed record ChatArchivo(string Id, string Canal, string Login, string Nombre, string Tipo, long Tamano, string Fecha, long? MensajeId);
public sealed class EstadoChat
{
    public long Global;
    public readonly long Inicio;
    private TaskCompletionSource<bool> senal = Nueva();
    public EstadoChat(long inicio) { Global = inicio; Inicio = inicio; }

    private static TaskCompletionSource<bool> Nueva() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Tarea que se completa en el próximo cambio (la esperan los Excel conectados).</summary>
    public Task Senal => Volatile.Read(ref senal).Task;

    /// <summary>Despierta a todos los que esperan.</summary>
    public void Pulsar() => Interlocked.Exchange(ref senal, Nueva()).TrySetResult(true);
}

public sealed record CanalChat(string Id, string Nombre, string Tipo, string Detalle, bool PuedeEscribir);

public static class ChatApi
{
    private const int MaxTexto = 2000;
    private const int MaxPorMinuto = 30;

    // Tipos de archivo que nunca se aceptan (programas, scripts, accesos directos, complementos)
    private static readonly HashSet<string> Bloqueadas = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".scr", ".pif", ".msi", ".msp", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".ps1", ".psm1", ".hta", ".cpl", ".jar", ".lnk", ".reg", ".dll", ".sys", ".application", ".gadget", ".inf",
        ".scf", ".url", ".xll", ".xlam", ".iso", ".img", ".vhd", ".vhdx"
    };

    private static readonly Dictionary<string, string> TiposMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".bmp"] = "image/bmp",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".csv"] = "text/csv",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [".xls"] = "application/vnd.ms-excel",
        [".xlsm"] = "application/vnd.ms-excel.sheet.macroEnabled.12",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [".doc"] = "application/msword",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation", [".zip"] = "application/zip"
    };

    private static string NombreSeguro(string? nombre)
    {
        string n = Path.GetFileName((nombre ?? "").Replace('\\', '/').Split('/').Last()).Trim();
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '|', '?', '*' })) n = n.Replace(ch, '_');
        if (n.Length > 120) n = n.Substring(0, 100) + Path.GetExtension(n);
        return n.Trim('.', ' ').Length == 0 ? "archivo" : n;
    }

    public static string IdPrivado(string a, string b)
    {
        var x = new[] { a.Trim().ToLowerInvariant(), b.Trim().ToLowerInvariant() };
        Array.Sort(x, StringComparer.Ordinal);
        return $"dm:{x[0]}|{x[1]}";
    }

    private static string RolTexto(string rol) => rol.ToUpperInvariant() switch
    {
        "ADMIN" => "Administrador",
        "JEFE" => "Jefe",
        "REGIONAL" => "Regional",
        _ => "Tienda"
    };

    /// <summary>Usuario reservado para los avisos automáticos (monitor de tiendas).</summary>
    public const string LoginSistema = "sistema";

    /// <summary>Publica un aviso automático en un canal (canal, texto). Lo asigna Mapear.</summary>
    public static Action<string, string>? PublicarSistema;

    public static void Mapear(WebApplication app, Almacen db, Func<HttpContext, Usuario?> autenticar,
                              Func<Usuario, List<string>> permitidas, Func<HttpContext, string> ip, bool generalSoloJefes,
                              int diasHistorial, string carpetaArchivos, int maxArchivoMB)
    {
        Directory.CreateDirectory(carpetaArchivos);
        long maxBytes = Math.Max(1, maxArchivoMB) * 1024L * 1024L;
        string RutaArchivo(string id) => Path.Combine(carpetaArchivos, id + ".bin");
        void BorrarSobrantes()
        {
            foreach (var id in db.ArchivosSobrantes())
            {
                try { File.Delete(RutaArchivo(id)); } catch { }
                db.BorrarArchivo(id);
            }
        }
        var envios = new ConcurrentDictionary<string, Queue<DateTime>>(StringComparer.OrdinalIgnoreCase);

        // ---- Marcas de cambio en memoria: si nada cambió se responde "sinCambios" sin tocar la base ----
        // (al reiniciar la API cambian todas las marcas y los Excel hacen una carga completa)
        var est = new EstadoChat(DateTime.UtcNow.Ticks);
        var versionCanal = new ConcurrentDictionary<string, long>();
        var versionLeido = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void CambioEn(string canal) { versionCanal[canal] = Interlocked.Increment(ref est.Global); est.Pulsar(); }
        void CambioLeido(string login) { versionLeido[login] = Interlocked.Increment(ref est.Global); est.Pulsar(); }
        string MarcaCanales(string login) =>
            Interlocked.Read(ref est.Global).ToString(CultureInfo.InvariantCulture) + "-" +
            (versionLeido.TryGetValue(login, out var v) ? v : 0).ToString(CultureInfo.InvariantCulture);
        string MarcaCanal(string canal) =>
            (versionCanal.TryGetValue(canal, out var v) ? v : est.Inicio).ToString(CultureInfo.InvariantCulture);

        // ---- "está escribiendo…": solo en memoria, dura 6 s si no se renueva ----
        var escribiendo = new ConcurrentDictionary<string, ConcurrentDictionary<string, (string nombre, DateTime hasta, DateTime pulso)>>();
        var versionEscribe = new ConcurrentDictionary<string, long>();
        long contadorEscribe = 0;
        void CambioEscribe(string canal) { versionEscribe[canal] = Interlocked.Increment(ref contadorEscribe); est.Pulsar(); }
        string MarcaEscribe(string canal) =>
            (versionEscribe.TryGetValue(canal, out var v) ? v : 0).ToString(CultureInfo.InvariantCulture);
        List<string> QuienesEscriben(string canal, string excepto)
        {
            if (!escribiendo.TryGetValue(canal, out var d)) return new List<string>();
            var ahora = DateTime.UtcNow;
            return d.Where(x => x.Value.hasta > ahora && !x.Key.Equals(excepto, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Value.nombre).OrderBy(n => n, StringComparer.CurrentCulture).ToList();
        }
        void DejoDeEscribir(string canal, string login)
        {
            if (escribiendo.TryGetValue(canal, out var d) && d.TryRemove(login, out (string, DateTime, DateTime) _)) CambioEscribe(canal);
        }

        // ---- Mantenimiento cada 6 h: purga del historial y limpieza del límite de envíos ----
        var mantenimiento = new Timer(_ =>
        {
            try
            {
                int n = db.PurgarChat(diasHistorial);
                if (n > 0) app.Logger.LogInformation("Chat: se borraron {N} mensajes de más de {D} días.", n, diasHistorial);
                BorrarSobrantes();
                var ahora = DateTime.UtcNow;
                foreach (var (canal, d) in escribiendo)
                {
                    foreach (var (login, x) in d) if (x.hasta < ahora) d.TryRemove(login, out (string, DateTime, DateTime) _);
                    if (d.IsEmpty) escribiendo.TryRemove(canal, out ConcurrentDictionary<string, (string nombre, DateTime hasta, DateTime pulso)>? _);
                }
                foreach (var k in envios.Keys)
                    if (envios.TryGetValue(k, out var q)) lock (q) { if (q.Count == 0 || (DateTime.Now - q.Last()).TotalMinutes > 5) envios.TryRemove(k, out Queue<DateTime>? _); }
            }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Chat: error en el mantenimiento"); }
        }, null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
        app.Lifetime.ApplicationStopping.Register(() => mantenimiento.Dispose());
        try { BorrarSobrantes(); } catch (Exception ex) { app.Logger.LogWarning(ex, "Chat: no se pudieron limpiar los archivos"); }
        IResult Error(int codigo, string mensaje) => Results.Json(new { error = mensaje }, statusCode: codigo);

        // Usuario válido para el chat (o la respuesta de error)
        IResult? Exigir(HttpContext ctx, out Usuario u)
        {
            u = autenticar(ctx)!;
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, "Su clave es temporal: debe cambiarla antes de continuar.");
            if (!u.Tiene("chat")) return Error(403, "No tiene permiso para usar el chat. Solicítelo al administrador.");
            return null;
        }

        // Comprueba el acceso al canal y devuelve su descripción (null = sin acceso)
        CanalChat? Resolver(Usuario u, string? canal, List<string>? tiendas = null)
        {
            if (string.IsNullOrWhiteSpace(canal)) return null;
            canal = canal.Trim();
            if (canal.Equals("general", StringComparison.OrdinalIgnoreCase))
                return new CanalChat("general", "General", "general",
                                     generalSoloJefes ? "Avisos para todos · escriben jefes y administradores" : "Todos los usuarios",
                                     !generalSoloJefes || u.GestionaUsuarios);
            if (canal.StartsWith("tienda:", StringComparison.OrdinalIgnoreCase))
            {
                string pedida = canal.Substring(7);
                var real = (tiendas ?? permitidas(u)).FirstOrDefault(t => t.Equals(pedida, StringComparison.CurrentCultureIgnoreCase));
                return real == null ? null : new CanalChat("tienda:" + real, real, "tienda", "Canal de la tienda", true);
            }
            if (canal.StartsWith("dm:", StringComparison.OrdinalIgnoreCase))
            {
                var partes = canal.Substring(3).Split('|');
                if (partes.Length != 2) return null;
                string yo = u.Login.ToLowerInvariant();
                if (partes[0] != yo && partes[1] != yo) return null;
                string otroLogin = partes[0] == yo ? partes[1] : partes[0];
                if (otroLogin == yo) return null;
                var otro = db.ObtenerUsuario(otroLogin);
                if (otro == null) return null;
                return new CanalChat(IdPrivado(u.Login, otro.Login), otro.Nombre, "privado",
                                     otro.Activo ? RolTexto(otro.Rol) + " · " + otro.Login : "Usuario inactivo", otro.Activo);
            }
            return null;
        }

        object Vista(ChatMensaje m)
        {
            ChatAdjuntoGuardado? adj = null;
            if (m.Adjunto != null) { try { adj = JsonSerializer.Deserialize<ChatAdjuntoGuardado>(m.Adjunto); } catch { } }
            bool esReporte = adj?.Sucursales is { Count: > 0 };
            bool esArchivo = adj?.ArchivoId != null;
            return new
            {
                id = m.Id, login = m.Login, nombre = m.Nombre, texto = m.Eliminado ? "" : m.Texto,
                fecha = m.Fecha, eliminado = m.Eliminado,
                adjunto = !esReporte ? null : new { sucursales = adj!.Sucursales, desde = adj.Desde, hasta = adj.Hasta },
                archivo = !esArchivo || m.Eliminado ? null : new
                {
                    id = adj!.ArchivoId, nombre = adj.ArchivoNombre, tamano = adj.ArchivoTamano, tipo = adj.ArchivoTipo,
                    imagen = (adj.ArchivoTipo ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                }
            };
        }

        string Resumen(ChatMensaje m)
        {
            if (m.Eliminado) return "Mensaje eliminado";
            if (m.Texto.Length > 0) return m.Texto;
            if (m.Adjunto == null) return "";
            try
            {
                var adj = JsonSerializer.Deserialize<ChatAdjuntoGuardado>(m.Adjunto);
                if (adj?.ArchivoId != null)
                    return (adj.ArchivoTipo ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "📷 Imagen" : "📎 " + adj.ArchivoNombre;
            }
            catch { }
            return "📊 Reporte compartido";
        }

        List<CanalChat> CanalesDe(Usuario u)
        {
            var tiendas = permitidas(u);
            var lista = new List<CanalChat> { Resolver(u, "general", tiendas)! };
            lista.AddRange(tiendas.Select(t => Resolver(u, "tienda:" + t, tiendas)!));
            foreach (var (canal, _) in db.PrivadosDe(u.Login))
            {
                var c = Resolver(u, canal, tiendas);
                if (c != null) lista.Add(c);
            }
            return lista;
        }

        PublicarSistema = (canal, texto) =>
        {
            db.InsertarMensaje(canal, LoginSistema, "Monitor de tiendas", texto, null);
            CambioEn(canal);
        };

        var chat = app.MapGroup("/api/chat");

        // Lista de conversaciones con no leídos y último mensaje (también sirve para el contador de la cinta)
        chat.MapGet("/canales", (HttpContext ctx, string? v) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            string marca = MarcaCanales(u.Login);                  // se lee ANTES de consultar
            if (!string.IsNullOrEmpty(v) && v == marca) return Results.Ok(new { sinCambios = true, version = marca });
            var canales = CanalesDe(u);
            var resumen = db.ResumenCanales(u.Login, canales.Select(c => c.Id).ToList());
            var salida = canales.Select(c =>
            {
                var (n, m) = resumen[c.Id];
                return new
                {
                    id = c.Id, nombre = c.Nombre, tipo = c.Tipo, detalle = c.Detalle, puedeEscribir = c.PuedeEscribir,
                    noLeidos = n,
                    ultimo = m == null ? null : new { id = m.Id, login = m.Login, nombre = m.Nombre, texto = Resumen(m), fecha = m.Fecha }
                };
            }).ToList();
            return Results.Ok(new { sinCambios = false, version = marca, total = salida.Sum(c => c.noLeidos), canales = salida });
        });

        // Tiempo real (long-polling): el Excel deja esta petición abierta y la API responde apenas hay
        // un cambio (mensaje nuevo, leído o eliminado) o a los 25 s si no pasó nada.
        // canal + ve: además avisa cuando cambia quién está escribiendo en la conversación abierta.
        chat.MapGet("/esperar", async (HttpContext ctx, string? v, string? canal, string? ve) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            var abierta = string.IsNullOrEmpty(canal) ? null : Resolver(u, canal);
            var limite = DateTime.UtcNow.AddSeconds(25);
            while (true)
            {
                var senal = est.Senal;                              // primero la señal, después la marca: no se pierde ningún cambio
                string marca = MarcaCanales(u.Login);
                string marcaE = abierta == null ? "" : MarcaEscribe(abierta.Id);
                bool cambio = string.IsNullOrEmpty(v) || marca != v;
                bool cambioE = abierta != null && marcaE != (ve ?? "");
                var resta = limite - DateTime.UtcNow;
                if (cambio || cambioE || resta <= TimeSpan.Zero)
                    return Results.Ok(new
                    {
                        cambio, version = marca, ve = marcaE,
                        escribiendo = abierta == null ? null : QuienesEscriben(abierta.Id, u.Login)
                    });
                await Task.WhenAny(senal, Task.Delay(resta, ctx.RequestAborted));
                if (ctx.RequestAborted.IsCancellationRequested) return Results.StatusCode(499);
            }
        });

        chat.MapGet("/contactos", (HttpContext ctx) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            var lista = db.ListarUsuarios()
                .Where(x => x.Activo && x.Tiene("chat") && !x.Login.Equals(u.Login, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Nombre, StringComparer.CurrentCulture)
                .Select(x => new { login = x.Login, nombre = x.Nombre, rol = x.Rol, rolTexto = RolTexto(x.Rol), canal = IdPrivado(u.Login, x.Login) })
                .ToList();
            return Results.Ok(new { contactos = lista });
        });

        // v = marca que tiene el Excel; visibleDesde = primer mensaje en pantalla (para avisar eliminaciones)
        chat.MapGet("/mensajes", (HttpContext ctx, string? canal, long? despues, long? antes, int? limite, string? v, long? visibleDesde,
                                  long? alrededor) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            var c = Resolver(u, canal);
            if (c == null) return Error(403, "No tiene acceso a esta conversación.");
            string marca = MarcaCanal(c.Id);
            var canalVista = new { id = c.Id, nombre = c.Nombre, tipo = c.Tipo, detalle = c.Detalle, puedeEscribir = c.PuedeEscribir };
            if ((alrededor ?? 0) > 0)
            {
                var (cerca, mas, nuevos) = db.MensajesAlrededor(c.Id, alrededor!.Value, 30);
                return Results.Ok(new
                {
                    sinCambios = false, version = marca, eliminados = new List<long>(), canal = canalVista,
                    mensajes = cerca.Select(Vista).ToList(), hayMas = mas, hayNuevos = nuevos
                });
            }
            if (!string.IsNullOrEmpty(v) && v == marca && (antes ?? 0) <= 0)
                return Results.Ok(new { sinCambios = true, version = marca });
            int lim = Math.Clamp(limite ?? 50, 1, 200);
            var lista = db.Mensajes(c.Id, despues ?? 0, antes ?? 0, lim);
            bool hayMas = (despues ?? 0) <= 0 && lista.Count == lim;
            var eliminados = !string.IsNullOrEmpty(v) && (visibleDesde ?? 0) > 0 ? db.EliminadosDesde(c.Id, visibleDesde!.Value) : new List<long>();
            return Results.Ok(new
            {
                sinCambios = false,
                version = marca,
                eliminados,
                canal = canalVista,
                mensajes = lista.Select(Vista).ToList(),
                hayMas,
                hayNuevos = false
            });
        });

        chat.MapPost("/mensajes", (ChatEnvioPeticion p, HttpContext ctx) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            var tiendas = permitidas(u);
            var c = Resolver(u, p.Canal, tiendas);
            if (c == null) return Error(403, "No tiene acceso a esta conversación.");
            if (!c.PuedeEscribir)
                return Error(403, c.Tipo == "privado" ? "Ese usuario está inactivo." : "En este canal solo escriben jefes y administradores.");

            string texto = (p.Texto ?? "").Trim();
            if (texto.Length > MaxTexto) return Error(400, $"El mensaje es demasiado largo (máximo {MaxTexto} caracteres).");

            string? adjunto = null;
            if (p.Adjunto != null)
            {
                var a = p.Adjunto;
                if (a.Desde == null || a.Hasta == null || a.Hasta < a.Desde) return Error(400, "Revise las fechas del reporte.");
                if ((a.Hasta.Value - a.Desde.Value).TotalDays > 400) return Error(400, "El rango máximo es de 400 días.");
                var suc = new List<string>();
                foreach (var s in a.Sucursales ?? new List<string>())
                {
                    var real = tiendas.FirstOrDefault(t => t.Equals(s, StringComparison.CurrentCultureIgnoreCase));
                    if (real == null) return Error(403, $"No tiene acceso a la tienda {s}.");
                    if (!suc.Contains(real)) suc.Add(real);
                }
                if (suc.Count == 0) return Error(400, "Seleccione al menos una tienda para el reporte.");
                adjunto = JsonSerializer.Serialize(new ChatAdjuntoGuardado(suc,
                    a.Desde.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    a.Hasta.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
            ChatArchivo? archivo = null;
            if (!string.IsNullOrWhiteSpace(p.Archivo))
            {
                if (adjunto != null) return Error(400, "Envíe el reporte y el archivo en mensajes separados.");
                archivo = db.ObtenerArchivo(p.Archivo.Trim());
                if (archivo == null || !archivo.Login.Equals(u.Login, StringComparison.OrdinalIgnoreCase) ||
                    archivo.Canal != c.Id || archivo.MensajeId != null || !File.Exists(RutaArchivo(archivo.Id)))
                    return Error(400, "El archivo ya no está disponible. Adjúntelo de nuevo.");
                adjunto = JsonSerializer.Serialize(new ChatAdjuntoGuardado(null, null, null, archivo.Id, archivo.Nombre, archivo.Tamano, archivo.Tipo));
            }
            if (texto.Length == 0 && adjunto == null) return Error(400, "Escriba un mensaje.");

            // Límite simple para evitar mensajes masivos por error
            var cola = envios.GetOrAdd(u.Login, _ => new Queue<DateTime>());
            lock (cola)
            {
                while (cola.Count > 0 && (DateTime.Now - cola.Peek()).TotalSeconds > 60) cola.Dequeue();
                if (cola.Count >= MaxPorMinuto) return Error(429, "Está enviando demasiados mensajes. Espere un momento.");
                cola.Enqueue(DateTime.Now);
            }

            if (c.Tipo == "privado")
            {
                var partes = c.Id.Substring(3).Split('|');
                db.RegistrarPrivado(c.Id, partes[0], partes[1]);
            }
            var m = db.InsertarMensaje(c.Id, u.Login, u.Nombre, texto, adjunto);
            if (archivo != null) db.VincularArchivo(archivo.Id, m.Id);
            DejoDeEscribir(c.Id, u.Login);
            CambioEn(c.Id);
            return Results.Ok(new { mensaje = Vista(m) });
        });

        chat.MapPost("/leido", (ChatLeidoPeticion p, HttpContext ctx) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            var c = Resolver(u, p.Canal);
            if (c == null) return Error(403, "No tiene acceso a esta conversación.");
            // solo se avisa a los demás equipos si de verdad cambió (si no, cada "leído" despertaba a todos en ciclo)
            if (p.HastaId > 0 && db.MarcarLeido(u.Login, c.Id, p.HastaId)) CambioLeido(u.Login);
            return Results.Ok(new { ok = true });
        });

        // Eliminar: el autor (sus propios mensajes) o un administrador (cualquiera)
        chat.MapDelete("/mensajes/{id:long}", (long id, HttpContext ctx) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            var m = db.ObtenerMensaje(id);
            if (m == null || Resolver(u, m.Canal) == null) return Error(404, "El mensaje no existe.");
            bool propio = m.Login.Equals(u.Login, StringComparison.OrdinalIgnoreCase);
            if (!propio && !u.EsAdmin) return Error(403, "Solo puede eliminar sus propios mensajes.");
            db.EliminarMensaje(id);
            try { BorrarSobrantes(); } catch { }                 // su archivo adjunto, si tenía
            CambioEn(m.Canal);
            if (!propio)
                db.Auditar(u.Login, "CHAT", null, null, null, null, $"Eliminó un mensaje de {m.Login} en {m.Canal}", ip(ctx));
            return Results.Ok(new { ok = true });
        });

        // ---- "está escribiendo…" (el Excel lo avisa cada 3 s mientras se escribe) ----
        chat.MapPost("/escribiendo", (ChatCanalPeticion p, HttpContext ctx) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            var c = Resolver(u, p.Canal);
            if (c == null || !c.PuedeEscribir) return Results.Ok(new { ok = false });
            var d = escribiendo.GetOrAdd(c.Id, _ => new ConcurrentDictionary<string, (string, DateTime, DateTime)>(StringComparer.OrdinalIgnoreCase));
            var ahora = DateTime.UtcNow;
            bool avisar = !d.TryGetValue(u.Login, out var previo) || previo.hasta < ahora || (ahora - previo.pulso).TotalSeconds >= 3;
            d[u.Login] = (ChatFormatoNombre(u.Nombre), ahora.AddSeconds(6), avisar ? ahora : previo.pulso);
            if (avisar) CambioEscribe(c.Id);
            return Results.Ok(new { ok = true });
        });

        // ---- Buscar en las conversaciones del usuario ----
        chat.MapGet("/buscar", (HttpContext ctx, string? q) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            string texto = (q ?? "").Trim();
            if (texto.Length < 2) return Error(400, "Escriba al menos 2 letras para buscar.");
            if (texto.Length > 100) texto = texto.Substring(0, 100);
            var canales = CanalesDe(u).ToDictionary(c => c.Id);
            var lista = db.BuscarMensajes(canales.Keys.ToList(), texto, 50);
            return Results.Ok(new
            {
                resultados = lista.Select(m =>
                {
                    var c = canales[m.Canal];
                    return new
                    {
                        id = m.Id, canal = c.Id, canalNombre = c.Nombre, canalTipo = c.Tipo,
                        login = m.Login, nombre = m.Nombre, texto = Resumen(m), fecha = m.Fecha
                    };
                }).ToList()
            });
        });

        // ---- Archivos e imágenes ----
        // Subir: el cuerpo es el archivo tal cual. Devuelve un id que luego se envía con el mensaje.
        chat.MapPost("/archivos", async (HttpContext ctx, string? canal, string? nombre) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            var c = Resolver(u, canal);
            if (c == null) return Error(403, "No tiene acceso a esta conversación.");
            if (!c.PuedeEscribir) return Error(403, "No puede enviar archivos en esta conversación.");
            string n = NombreSeguro(nombre);
            string ext = Path.GetExtension(n);
            if (Bloqueadas.Contains(ext)) return Error(400, $"Por seguridad no se permiten archivos {ext}.");
            if (ctx.Request.ContentLength > maxBytes) return Error(413, $"El archivo supera el máximo de {maxArchivoMB} MB.");

            var cola = envios.GetOrAdd(u.Login, _ => new Queue<DateTime>());
            lock (cola)
            {
                while (cola.Count > 0 && (DateTime.Now - cola.Peek()).TotalSeconds > 60) cola.Dequeue();
                if (cola.Count >= MaxPorMinuto) return Error(429, "Está enviando demasiados mensajes. Espere un momento.");
                cola.Enqueue(DateTime.Now);
            }

            string id = Guid.NewGuid().ToString("N");
            string ruta = RutaArchivo(id);
            long total = 0;
            try
            {
                await using (var fs = new FileStream(ruta, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buf = new byte[81920];
                    int leidos;
                    while ((leidos = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
                    {
                        total += leidos;
                        if (total > maxBytes) break;
                        await fs.WriteAsync(buf.AsMemory(0, leidos), ctx.RequestAborted);
                    }
                }
                if (total > maxBytes) { File.Delete(ruta); return Error(413, $"El archivo supera el máximo de {maxArchivoMB} MB."); }
                if (total == 0) { File.Delete(ruta); return Error(400, "El archivo está vacío."); }
            }
            catch (Exception ex)
            {
                try { File.Delete(ruta); } catch { }
                if (ctx.RequestAborted.IsCancellationRequested) return Results.StatusCode(499);
                app.Logger.LogWarning(ex, "Chat: error al recibir un archivo");
                return Error(500, "No se pudo guardar el archivo.");
            }
            string tipo = TiposMime.TryGetValue(ext, out var t) ? t : "application/octet-stream";
            db.RegistrarArchivo(new ChatArchivo(id, c.Id, u.Login, n, tipo, total,
                                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), null));
            return Results.Ok(new { id, nombre = n, tamano = total, tipo, imagen = tipo.StartsWith("image/") });
        });

        // Descargar: solo quien tiene acceso a la conversación del mensaje
        chat.MapGet("/archivos/{id}", (string id, HttpContext ctx) =>
        {
            var e = Exigir(ctx, out var u);
            if (e != null) return e;
            if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return Error(404, "El archivo no existe.");
            var a = db.ObtenerArchivo(id);
            if (a == null || Resolver(u, a.Canal) == null) return Error(404, "El archivo no existe.");
            if (a.MensajeId == null && !a.Login.Equals(u.Login, StringComparison.OrdinalIgnoreCase)) return Error(404, "El archivo no existe.");
            string ruta = RutaArchivo(a.Id);
            if (!File.Exists(ruta)) return Error(404, "El archivo ya no está disponible (se eliminó el mensaje o venció el historial).");
            return Results.File(ruta, a.Tipo, a.Nombre, enableRangeProcessing: false);
        });
    }

    private static string ChatFormatoNombre(string nombre)
    {
        var p = (nombre ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 0 ? "Alguien" : p[0];
    }
}
