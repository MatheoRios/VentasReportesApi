// Generado al dividir Program.cs por módulos: el código de cada parte es el mismo que estaba en Program.cs.
namespace VentasReportesApi;

/// <summary>
/// La API: estado compartido (almacén, tokens, caché, límites) y la ayuda común de todos los endpoints.
/// Cada grupo de endpoints está en su archivo: Servidor.Sesion.cs, Servidor.Reportes.cs, Servidor.Usuarios.cs, Servidor.Tiendas.cs.
/// </summary>
public sealed partial class Servidor
{
    private readonly WebApplication app;
    private readonly Almacen almacen;
    private readonly Cifrado cifrado;
    private readonly string datosDir;
    private readonly IConfigurationSection cfg;
    private readonly double horasSesion;
    private readonly int timeoutSeg, intentos, minutosBloqueo, cacheHoySeg, cachePasadoMin;
    private readonly Concurrencia concurrencia;
    private readonly Compartidos compartidos;
    private readonly Tokens tokens;
    private readonly string version;
    private RouteGroupBuilder adminApi = null!;
    private Timer? limpiezaCache;

    public string Version => version;
    public int TiendasConfiguradas => TiendasActivas().Count;

    public Servidor(WebApplication app, Almacen almacen, Cifrado cifrado, string datosDir)
    {
        this.app = app;
        this.almacen = almacen;
        this.cifrado = cifrado;
        this.datosDir = datosDir;
        cfg = app.Configuration.GetSection("Reportes");
        horasSesion = cfg.GetValue("HorasSesion", 8.0);
        timeoutSeg = cfg.GetValue("TiempoConsultaSegundos", 300);
        intentos = cfg.GetValue("IntentosAntesDeBloqueo", 5);
        minutosBloqueo = cfg.GetValue("MinutosBloqueo", 15);
        // ---- Concurrencia hacia los POS (Concurrencia.cs) ----
        concurrencia = new Concurrencia(
            global: cfg.GetValue("MaxConsultasSimultaneas", 24),
            porTienda: cfg.GetValue("MaxConsultasPorTienda", 2),
            porUsuario: cfg.GetValue("MaxConsultasPorUsuario", 8),
            espera: TimeSpan.FromSeconds(cfg.GetValue("EsperaMaximaTurnoSegundos", 120)));
        compartidos = new Compartidos(pesoMaximo: cfg.GetValue("CacheFilasMaximas", 300000));
        Lectura.SinBloqueo = cfg.GetValue("LecturaSinBloqueo", true);
        cacheHoySeg = cfg.GetValue("CacheHoySegundos", 60);
        cachePasadoMin = cfg.GetValue("CacheDiasPasadosMinutos", 10);
        tokens = new Tokens(Path.Combine(datosDir, "token.key"));
        version = typeof(Almacen).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        almacen.InicializarChat(cfg.GetValue("DiasHistorialChat", 365));
    }

    /// <summary>Registra todos los endpoints (mismo orden que antes).</summary>
    public void Mapear()
    {
        MapearSesion();
        MapearEstadoTiendas();
        // Mantenimiento de la caché de reportes (saca lo vencido) y límite de intentos de login por IP
        limpiezaCache = new Timer(_ =>
        {
            try { compartidos.Recortar(); } catch { }
            try { LimpiarIntentosLoginIp(); } catch { }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        app.Lifetime.ApplicationStopping.Register(() => limpiezaCache.Dispose());
        MapearReportes();
        adminApi = app.MapGroup("/api/admin");
        MapearUsuarios();
        MapearTiendas();
        MapearMiCuenta();
        MapearAuditoria();
        MapearModulos();
    }

    // Resuelve y descifra SOLO la tienda solicitada (evita descifrar las 24 claves en cada reporte o en aciertos de caché)
    string? CadenaDeTienda(string nombre)
    {
        var t = almacen.ObtenerTienda(nombre);
        if (t == null || !t.Activa) return null;
        // 1 reintento: una tienda apagada falla en ~1 min en lugar de ~2,5 min y no frena el reporte
        return Conexiones.Cadena(t.Servidor, t.Puerto, t.BaseDatos, t.Usuario, cifrado.Descifrar(t.ClaveCifrada), t.TimeoutSeg, reintentos: 1);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Queue<DateTime>> intentosLoginIp = new(StringComparer.OrdinalIgnoreCase);

    bool PermitirIntentoLoginIp(string ip)
    {
        string clave = string.IsNullOrWhiteSpace(ip) ? "desconocida" : ip;
        var cola = intentosLoginIp.GetOrAdd(clave, _ => new Queue<DateTime>());
        var ahora = DateTime.UtcNow;
        lock (cola)
        {
            while (cola.Count > 0 && (ahora - cola.Peek()).TotalSeconds > 60) cola.Dequeue();
            if (cola.Count >= 30) return false;
            cola.Enqueue(ahora);
            return true;
        }
    }

    private void LimpiarIntentosLoginIp()
    {
        var ahora = DateTime.UtcNow;
        foreach (var kv in intentosLoginIp)
        {
            bool quitar = false;
            lock (kv.Value)
            {
                while (kv.Value.Count > 0 && (ahora - kv.Value.Peek()).TotalSeconds > 60) kv.Value.Dequeue();
                quitar = kv.Value.Count == 0;
            }
            if (quitar) intentosLoginIp.TryRemove(kv.Key, out _);
        }
    }

    string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "";

    Usuario? Autenticar(HttpContext ctx)
    {
        string h = ctx.Request.Headers.Authorization.ToString();
        if (!h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var v = tokens.ValidarConVersion(h.Substring(7).Trim());
        if (v == null) return null;
        var u = almacen.ObtenerUsuario(v.Value.login);
        return u is { Activo: true } && u.TokenVersion == v.Value.tokenVersion ? u : null;   // desactivar o cambiar clave = acceso cortado al instante
    }

    // Tiendas que puede ver el usuario. Solo usa los nombres: no descifra claves.
    List<string> PermitidasDe(Usuario u, HashSet<string> activas)
    {
        if (u.VeTodas) return activas.OrderBy(k => k, StringComparer.CurrentCulture).ToList();
        return almacen.SucursalesDe(u.Id).Where(activas.Contains)
                      .OrderBy(k => k, StringComparer.CurrentCulture).ToList();
    }

    HashSet<string> TiendasActivas() =>
        almacen.ListarTiendas().Where(t => t.Activa).Select(t => t.Nombre).ToHashSet(StringComparer.CurrentCultureIgnoreCase);

    List<string> Permitidas(Usuario u) => PermitidasDe(u, TiendasActivas());

    // Para el chat (consultas cada pocos segundos): nombres de tiendas activas en memoria por 30 s
    private (DateTime cuando, HashSet<string> nombres) cacheTiendas = (DateTime.MinValue, new HashSet<string>());
    private readonly object bloqueoTiendas = new();
    List<string> PermitidasChat(Usuario u)
    {
        HashSet<string> activas;
        lock (bloqueoTiendas)
        {
            if ((DateTime.Now - cacheTiendas.cuando).TotalSeconds > 30) cacheTiendas = (DateTime.Now, TiendasActivas());
            activas = cacheTiendas.nombres;
        }
        return PermitidasDe(u, activas);
    }

    IResult Error(int codigo, string mensaje) => Results.Json(new { error = mensaje }, statusCode: codigo);
    const string ClaveTemporal = "Su clave es temporal: debe cambiarla antes de continuar.";

    // ------------------------------------------------------------------ estado de las tiendas
    // Prueba rápida (3 s) de cada tienda permitida; el resultado se guarda 60 s para no saturar los servidores.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime cuando, bool ok, long ms, string msg)> cacheEstado = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly SemaphoreSlim limiteEstado = new(12);

    async Task<(bool ok, long ms, string msg)> EstadoTiendaAsync(Tienda t, CancellationToken ct)
    {
        if (cacheEstado.TryGetValue(t.Nombre, out var c) && (DateTime.Now - c.cuando).TotalSeconds < 60) return (c.ok, c.ms, c.msg);
        await limiteEstado.WaitAsync(ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        (bool ok, long ms, string msg) r;
        try
        {
            string cad = Conexiones.Cadena(t.Servidor, t.Puerto, t.BaseDatos, t.Usuario, cifrado.Descifrar(t.ClaveCifrada), 3, reintentos: 0);
            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(cad);
            await conn.OpenAsync(ct);
            r = (true, sw.ElapsedMilliseconds, $"En línea ({sw.ElapsedMilliseconds:N0} ms)");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            r = (false, sw.ElapsedMilliseconds, ConsultaSucursal.MensajeAmigable(ex));
        }
        finally { limiteEstado.Release(); }
        cacheEstado[t.Nombre] = (DateTime.Now, r.ok, r.ms, r.msg);
        return r;
    }

    // Ejecuta la consulta de UNA tienda respetando los límites, compartiendo consultas iguales y con caché corta.
    async Task<IResult> ConsultarTienda<T>(HttpContext ctx, Usuario u, string tipo, string tienda, DateTime desde, DateTime hasta,
                                           string extra, string etiqueta, Func<CancellationToken, Task<T>> consulta,
                                           Func<T, int> filas, Func<T, object> respuesta) where T : class
    {
        var ct = ctx.RequestAborted;
        string clave = $"{tipo}|{tienda}|{desde:yyyyMMdd}|{hasta:yyyyMMdd}|{extra}".ToUpperInvariant();
        // con el día de hoy las ventas siguen entrando: caché muy corta; días cerrados: unos minutos
        var vida = hasta.Date >= DateTime.Today ? TimeSpan.FromSeconds(cacheHoySeg) : TimeSpan.FromMinutes(cachePasadoMin);
        try
        {
            var (r, origen) = await compartidos.ObtenerAsync(clave, vida, async tk =>
            {
                using var turno = await concurrencia.EntrarAsync(tienda, u.Login, tk);
                if (turno == null) throw new ServidorOcupadoException();
                return await consulta(tk);
            }, x => filas(x), 50000, ct);
            string nota = origen == OrigenResultado.Cache ? " [caché]" : origen == OrigenResultado.Compartido ? " [compartida]" : "";
            almacen.Auditar(u.Login, "REPORTE", tienda, desde, hasta, filas(r), "OK" + etiqueta + nota, Ip(ctx));
            return Results.Ok(respuesta(r));
        }
        catch (ServidorOcupadoException)
        {
            app.Logger.LogWarning("Sin turno para {Tienda} ({Usuario}): {Activas} consultas activas, {Cola} en cola",
                                  tienda, u.Login, concurrencia.Activas, concurrencia.EnCola);
            almacen.Auditar(u.Login, "REPORTE", tienda, desde, hasta, null, "Servidor ocupado" + etiqueta, Ip(ctx));
            return Error(503, "El servidor está atendiendo muchas consultas en este momento. Se reintentará en unos segundos.");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            almacen.Auditar(u.Login, "REPORTE", tienda, desde, hasta, null, "Cancelado por el usuario" + etiqueta, Ip(ctx));
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Error consultando {Tienda} ({Tipo})", tienda, tipo);
            string msg = ex is OperationCanceledException ? "La tienda no respondió a tiempo."
                       : ex is Microsoft.Data.SqlClient.SqlException sx && (sx.Number == 208 || sx.Number == 207)
                           ? "La base de la tienda no tiene las tablas o columnas KLK que usa el reporte (" + sx.Message + ")"
                           : ConsultaSucursal.MensajeAmigable(ex);
            almacen.Auditar(u.Login, "REPORTE", tienda, desde, hasta, null, "Error" + etiqueta + ": " + msg, Ip(ctx));
            return Error(502, msg);
        }
    }


    // ------------------------------------------------------------------ administración
    //  ADMIN: todo.  JEFE: crea/edita usuarios TIENDA y REGIONAL y les asigna tiendas; NO ve conexiones.
    private readonly System.Text.RegularExpressions.Regex regexLogin = new("^[A-Za-z0-9._-]{3,30}$");

    IResult? ExigirAdmin(HttpContext ctx, out Usuario admin)
    {
        admin = Autenticar(ctx)!;
        if (admin == null) return Error(401, "Sesión vencida o inválida.");
        if (admin.DebeCambiarClave) return Error(403, ClaveTemporal);
        if (!admin.EsAdmin) return Error(403, "Solo los administradores pueden ver o cambiar las conexiones de las tiendas.");
        return null;
    }

    IResult? ExigirGestor(HttpContext ctx, out Usuario gestor)
    {
        gestor = Autenticar(ctx)!;
        if (gestor == null) return Error(401, "Sesión vencida o inválida.");
        if (gestor.DebeCambiarClave) return Error(403, ClaveTemporal);
        if (!gestor.GestionaUsuarios) return Error(403, "Solo administradores y jefes pueden gestionar usuarios.");
        return null;
    }

    const string SinPermisoJefe = "Como Jefe solo puede crear o modificar usuarios con rol Tienda o Regional.";

    object VistaUsuario(Usuario x, Usuario quien) => new
    {
        editable = quien.PuedeEditar(x),
        login = x.Login,
        nombre = x.Nombre,
        rol = x.Rol,
        activo = x.Activo,
        bloqueado = x.BloqueadoHasta > DateTime.Now,
        ultimoAcceso = x.UltimoAcceso?.ToString("dd/MM/yyyy HH:mm"),
        tiendas = almacen.SucursalesDe(x.Id),
        permisos = x.ListaPermisos
    };

    // Valida la petición; devuelve el error o null si todo está bien
    string? Validar(UsuarioPeticion p, bool nuevo, out string rol, out List<string> tiendas)
    {
        rol = (p.Rol ?? "").Trim().ToUpperInvariant();
        tiendas = new List<string>();
        if (nuevo && (p.Login == null || !regexLogin.IsMatch(p.Login.Trim())))
            return "El usuario debe tener de 3 a 30 caracteres: letras, números, punto, guion o guion bajo (sin espacios).";
        if (nuevo && p.Login!.Trim().Equals(ChatApi.LoginSistema, StringComparison.OrdinalIgnoreCase))
            return "El nombre de usuario 'sistema' está reservado.";
        if (string.IsNullOrWhiteSpace(p.Nombre)) return "Escriba el nombre completo.";
        if (!Almacen.Roles.Contains(rol)) return "Seleccione un rol válido.";
        if (nuevo && string.IsNullOrEmpty(p.Clave)) return "Escriba una clave para el usuario nuevo.";
        if (!string.IsNullOrEmpty(p.Clave)) { var e = Claves.ValidarPolitica(p.Clave); if (e != null) return e; }

        var activas = TiendasActivas();
        foreach (var t in p.Tiendas ?? new List<string>())
        {
            var real = activas.FirstOrDefault(k => k.Equals(t, StringComparison.CurrentCultureIgnoreCase));
            if (real != null && !tiendas.Contains(real)) tiendas.Add(real);
        }
        if (rol is not ("ADMIN" or "JEFE") && tiendas.Count == 0) return "Asigne al menos una tienda (o use el rol Jefe o Administrador).";
        return null;
    }


    // ------------------------------------------------------------------ administración de tiendas (solo ADMIN)
    object VistaTienda(Tienda t, Dictionary<string, int> usos) => new
    {
        nombre = t.Nombre,
        servidor = t.Servidor,
        puerto = t.Puerto,
        baseDatos = t.BaseDatos,
        usuario = t.Usuario,
        timeout = t.TimeoutSeg,
        activa = t.Activa,
        modificado = t.Modificado,
        usuariosAsignados = usos.TryGetValue(t.Nombre, out var n) ? n : 0
    };

    string? ValidarTienda(TiendaPeticion p, bool nueva)
    {
        if (string.IsNullOrWhiteSpace(p.Nombre) || p.Nombre.Trim().Length > 40) return "Escriba el nombre de la tienda (máximo 40 caracteres).";
        if (p.Nombre.IndexOfAny(new[] { ',', '|', ';', ':' }) >= 0) return "El nombre no puede tener , | ; :";
        if (string.IsNullOrWhiteSpace(p.Servidor)) return "Escriba el servidor (IP o nombre).";
        if (p.Puerto is < 1 or > 65535) return "El puerto debe estar entre 1 y 65535 (o vacío).";
        if (string.IsNullOrWhiteSpace(p.BaseDatos)) return "Escriba la base de datos.";
        if (string.IsNullOrWhiteSpace(p.Usuario)) return "Escriba el usuario SQL.";
        if (nueva && string.IsNullOrEmpty(p.Clave)) return "Escriba la clave SQL.";
        if (p.Timeout is < 5 or > 600) return "El tiempo de conexión debe estar entre 5 y 600 segundos.";
        return null;
    }

    Tienda DesdePeticion(TiendaPeticion p) => new()
    {
        Nombre = p.Nombre!.Trim().ToUpperInvariant(),
        Servidor = p.Servidor!.Trim(),
        Puerto = p.Puerto,
        BaseDatos = p.BaseDatos!.Trim(),
        Usuario = p.Usuario!.Trim(),
        TimeoutSeg = p.Timeout ?? 30,
        Activa = p.Activa ?? true
    };


    // ---- módulos en sus propios archivos: chat, complemento, huella y monitor ----
    private void MapearModulos()
    {
        // ------------------------------------------------------------------ chat entre usuarios (Chat.cs)
        ChatApi.Mapear(app, almacen, Autenticar, PermitidasChat, Ip, cfg.GetValue("ChatGeneralSoloJefes", false),
                       cfg.GetValue("DiasHistorialChat", 365), Path.Combine(datosDir, "adjuntos"), cfg.GetValue("ChatMaxArchivoMB", 10));
        ComplementoApi.Mapear(app, almacen, Path.Combine(datosDir, "complemento"),
                              ctx => { var e = ExigirAdmin(ctx, out var a); return (e, e == null ? a : null); }, Ip,
                              cfg.GetValue<string>("UrlPublica") ?? cfg.GetValue<string>("OrigenWeb"));

        // ------------------------------------------------------------------ ingreso con huella / Face ID (Huella.cs)
        // Solo por HTTPS: Reportes:OrigenWeb = dirección exacta de la app, por ejemplo https://servidor.tail1234.ts.net
        HuellaApi.Mapear(app, almacen, tokens, horasSesion, Autenticar, Ip, cfg.GetValue<string>("OrigenWeb"));

        // ------------------------------------------------------------------ monitor de tiendas (MonitorTiendas.cs)
        // Prueba cada tienda y avisa en su canal del chat cuando deja de responder o se recupera.
        MonitorTiendas.Iniciar(app, almacen, app.Configuration, async (t, ct) =>
        {
            cacheEstado.TryRemove(t.Nombre, out _);           // prueba real; de paso refresca los puntos verde/rojo
            return await EstadoTiendaAsync(t, ct);
        });

    }
}
