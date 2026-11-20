using System.IO.Compression;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using VentasReportesApi;

// =====================================================================================
//  API de Reportes de Ventas POS
//  - Usuarios, permisos y auditoría en datos\seguridad.db (SQLite)
//  - Conexiones a las tiendas en la misma base, con la clave SQL cifrada (AES-GCM)
//  - Se administran desde Excel (Usuarios y accesos → Tiendas); el complemento no ve las claves
// =====================================================================================

string baseDir = AppContext.BaseDirectory;
string datosDir = Path.Combine(baseDir, "datos");
Directory.CreateDirectory(datosDir);

var almacen = new Almacen(Path.Combine(datosDir, "seguridad.db"));
almacen.Inicializar();
almacen.Migrar();
almacen.InicializarTiendas();
almacen.InicializarHuellas();
var cifrado = new Cifrado(Path.Combine(datosDir, "tiendas.key"));
int importadas = Conexiones.ImportarJson(Path.Combine(baseDir, "sucursales.json"), almacen, cifrado);

// ---- Modo administración por consola:  VentasReportesApi usuario-crear ... ----
if (args.Length > 0 && !args[0].StartsWith("--"))
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    if (args[0].Equals("importar-config", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 2 || !File.Exists(args[1])) { Console.WriteLine("Uso: VentasReportesApi importar-config <archivo.config>"); return 1; }
        var res = ImportadorConfig.Importar(File.ReadAllText(args[1]), almacen, cifrado, aplicar: true);
        Console.WriteLine($"Nuevas ({res.Nuevas.Count}): {string.Join(", ", res.Nuevas)}");
        Console.WriteLine($"Actualizadas ({res.Actualizadas.Count}):"); res.Actualizadas.ForEach(x => Console.WriteLine("  " + x));
        Console.WriteLine($"Sin cambios ({res.SinCambios.Count}): {string.Join(", ", res.SinCambios)}");
        if (res.NoIncluidas.Count > 0) Console.WriteLine($"En el servidor pero no en el archivo (no se tocaron): {string.Join(", ", res.NoIncluidas)}");
        res.Avisos.ForEach(x => Console.WriteLine("! " + x));
        res.Errores.ForEach(x => Console.WriteLine("✖ " + x));
        almacen.Auditar(Environment.UserName, "TIENDA", resultado: $"Importó tiendas por consola: {res.Nuevas.Count} nuevas, {res.Actualizadas.Count} actualizadas");
        return res.Errores.Count > 0 && res.Nuevas.Count + res.Actualizadas.Count == 0 ? 1 : 0;
    }
    var nombres = almacen.ListarTiendas().Select(t => t.Nombre).ToList();
    return Consola.Ejecutar(args, almacen, nombres);
}

// ---- Modo servidor ----
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = baseDir });
builder.Host.UseWindowsService(o => o.ServiceName = "VentasReportesApi");
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

var app = builder.Build();
// Detrás de Tailscale Serve (HTTPS) las peticiones llegan desde 127.0.0.1: se toma la IP real del encabezado
// X-Forwarded-For solo si viene de la propia máquina (proxies conocidos = loopback, el valor por defecto).
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.UseResponseCompression();

// ---- App web para teléfono, tablet y PC (carpeta wwwroot/app): misma API, mismos usuarios y permisos ----
app.Use(async (ctx, next) =>
{
    // "/app" sin barra final → "/app/" (solo ese caso exacto: así no se forma un ciclo de redirecciones)
    if (ctx.Request.Path.Value == "/app") { ctx.Response.Redirect("/app/" + ctx.Request.QueryString); return; }
    if (ctx.Request.Path.StartsWithSegments("/app"))
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
                                       "font-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c =>
    {
        // la app se actualiza al instante (HTML, JS y CSS se revalidan); fuentes e íconos se guardan una semana
        string ext = Path.GetExtension(c.File.Name).ToLowerInvariant();
        c.Context.Response.Headers.CacheControl = ext is ".woff2" or ".png" ? "public, max-age=604800" : "no-cache";
    }
});
app.MapGet("/", () => Results.Redirect("/app/"));

// ---- Endpoints de la API (Servidor.*.cs) ----
var servidor = new Servidor(app, almacen, cifrado, datosDir);
servidor.Mapear();

if (importadas > 0)
    app.Logger.LogWarning("Se importaron {N} tiendas desde sucursales.json a la base (clave cifrada). El archivo quedó como sucursales.json.importado: bórrelo cuando verifique que todo funciona.", importadas);

if (almacen.ContarUsuarios() == 0)
    app.Logger.LogWarning("No hay usuarios. Cree el primero con:  VentasReportesApi usuario-crear admin \"Administrador\" ADMIN");

app.Logger.LogInformation("API de Reportes {Version} lista. Tiendas configuradas: {N}", servidor.Version, servidor.TiendasConfiguradas);
app.Run();
return 0;

