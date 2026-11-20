using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VentasReportesApi;

// =====================================================================================
//  Distribución del complemento de Excel desde el servidor
//   - El administrador publica los .xll (32 y 64 bits) con publicar-complemento.bat.
//   - Cada Excel revisa /api/complemento y se actualiza solo.
//   - Una PC nueva se instala desde http://SERVIDOR:5080/instalar (sin USB).
//  Archivos en datos/complemento: ReportesVentasPOS64.xll, ReportesVentasPOS32.xll, version.json
// =====================================================================================

public sealed record ComplementoArchivo(string Sha256, long Tamano);
public sealed record ComplementoManifiesto(string Version, string Fecha, string? Notas, string? PublicadoPor,
                                           ComplementoArchivo? X64, ComplementoArchivo? X86);
public sealed record ComplementoPublicar(string? Version, string? Notas);

public static class ComplementoApi
{
    private const long MaxBytes = 60L * 1024 * 1024;
    private static readonly object bloqueo = new();

    private static string Bits(string bits) => bits == "64" ? "64" : bits == "32" ? "32" : "";

    /// <summary>¿El .xll trae el .dna y el código empaquetados? (nombres de recurso en UTF-16 que agrega ExcelDnaPack)</summary>
    private static bool Empaquetado(string ruta)
    {
        var bytes = File.ReadAllBytes(ruta);
        bool Tiene(string texto) => bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(texto)) >= 0;
        return Tiene("__MAIN__") && Tiene("VENTASEXCELFUNCTIONS");
    }

    private static readonly System.Text.RegularExpressions.Regex RegexHostSeguro =
        new(@"^[A-Za-z0-9.\-:\[\]]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? NormalizarUrlBase(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (!RegexHostSeguro.IsMatch(uri.Host)) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static void Mapear(WebApplication app, Almacen db, string carpeta,
                              Func<HttpContext, (IResult? error, Usuario? admin)> exigirAdmin, Func<HttpContext, string> ip,
                              string? urlPublica = null)
    {
        Directory.CreateDirectory(carpeta);
        string Ruta(string bits) => Path.Combine(carpeta, $"ReportesVentasPOS{bits}.xll");
        string RutaPendiente(string bits) => Path.Combine(carpeta, $"pendiente{bits}.xll");
        string RutaManifiesto = Path.Combine(carpeta, "version.json");
        string? urlFija = NormalizarUrlBase(urlPublica);
        IResult Error(int codigo, string mensaje) => Results.Json(new { error = mensaje }, statusCode: codigo);

        ComplementoManifiesto? Leer()
        {
            try { return File.Exists(RutaManifiesto) ? JsonSerializer.Deserialize<ComplementoManifiesto>(File.ReadAllText(RutaManifiesto)) : null; }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Complemento: version.json dañado"); return null; }
        }

        static string Sha(string ruta)
        {
            using var fs = File.OpenRead(ruta);
            return Convert.ToHexString(SHA256.HashData(fs));        // MAYÚSCULAS, igual que Get-FileHash
        }

        // Usa la URL configurada (Reportes:UrlPublica o OrigenWeb) cuando existe; si no, valida estrictamente el Host
        // para impedir inyección de comandos en instalar.ps1 / InstalarReportesPOS.cmd.
        string? UrlBase(HttpContext ctx)
        {
            if (urlFija != null) return urlFija;
            string scheme = ctx.Request.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
            var host = ctx.Request.Host;
            if (!host.HasValue || string.IsNullOrWhiteSpace(host.Host) || !RegexHostSeguro.IsMatch(host.Host))
                return null;
            string authority = host.Port.HasValue ? $"{host.Host}:{host.Port.Value}" : host.Host;
            return NormalizarUrlBase($"{scheme}://{authority}");
        }

        // ---- Público (sin sesión): lo usan el instalador y cada Excel para buscar actualizaciones ----
        app.MapGet("/api/complemento", () =>
        {
            var m = Leer();
            if (m == null) return Results.Ok(new { disponible = false });
            return Results.Ok(new
            {
                disponible = m.X64 != null || m.X86 != null,
                version = m.Version, fecha = m.Fecha, notas = m.Notas,
                x64 = m.X64 == null ? null : new { sha256 = m.X64.Sha256, tamano = m.X64.Tamano },
                x86 = m.X86 == null ? null : new { sha256 = m.X86.Sha256, tamano = m.X86.Tamano }
            });
        });

        app.MapGet("/api/complemento/descargar/{bits}", (string bits) =>
        {
            bits = Bits(bits);
            if (bits.Length == 0) return Error(400, "Indique 32 o 64 bits.");
            string ruta = Ruta(bits);
            if (!File.Exists(ruta)) return Error(404, $"Todavía no hay complemento de {bits} bits publicado en el servidor.");
            return Results.File(ruta, "application/octet-stream", "ReportesVentasPOS.xll");
        });

        // ---- Administración: subir y publicar una versión ----
        // 1) PUT /api/admin/complemento/64 y /32 con el .xll como cuerpo  2) POST /api/admin/complemento/publicar
        app.MapPut("/api/admin/complemento/{bits}", async (string bits, HttpContext ctx) =>
        {
            var (e, admin) = exigirAdmin(ctx);
            if (e != null) return e;
            bits = Bits(bits);
            if (bits.Length == 0) return Error(400, "Indique 32 o 64 bits.");
            if (ctx.Request.ContentLength > MaxBytes) return Error(413, "El archivo es demasiado grande.");
            string tmp = RutaPendiente(bits) + ".subiendo";
            long total = 0;
            var cabecera = new byte[2];
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
                {
                    if (total < 2) Array.Copy(buf, 0, cabecera, (int)total, (int)Math.Min(2 - total, n));
                    total += n;
                    if (total > MaxBytes) break;
                    await fs.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                }
            }
            if (total > MaxBytes) { File.Delete(tmp); return Error(413, "El archivo es demasiado grande."); }
            if (total < 1024 || cabecera[0] != (byte)'M' || cabecera[1] != (byte)'Z')
            {
                File.Delete(tmp);
                return Error(400, "El archivo no es un complemento .xll válido.");
            }
            // Un .xll sin empaquetar (VentasExcelFunctions-AddIn64.xll en vez de ...-packed.xll) no trae el .dna ni el
            // código del complemento: en las PCs da "requires an '<addin>.dna' configuration file". Se rechaza aquí.
            if (!Empaquetado(tmp))
            {
                File.Delete(tmp);
                return Error(400, "Ese .xll no está empaquetado (le falta el .dna y el código del complemento). Suba el archivo «-packed.xll»: ejecute preparar-instalador.bat cuando Visual Studio haya terminado de compilar.");
            }
            File.Move(tmp, RutaPendiente(bits), true);
            return Results.Ok(new { bits, tamano = total, sha256 = Sha(RutaPendiente(bits)) });
        });

        app.MapPost("/api/admin/complemento/publicar", (ComplementoPublicar p, HttpContext ctx) =>
        {
            var (e, admin) = exigirAdmin(ctx);
            if (e != null) return e;
            string version = (p.Version ?? "").Trim();
            if (version.Length == 0 || version.Length > 40) return Error(400, "Indique la versión (máximo 40 caracteres).");
            bool hay64 = File.Exists(RutaPendiente("64")), hay32 = File.Exists(RutaPendiente("32"));
            if (!hay64 && !hay32) return Error(400, "Primero suba el archivo de 64 y/o 32 bits.");

            ComplementoManifiesto nuevo;
            lock (bloqueo)
            {
                var anterior = Leer();
                foreach (var b in new[] { "64", "32" })
                    if (File.Exists(RutaPendiente(b))) File.Move(RutaPendiente(b), Ruta(b), true);
                ComplementoArchivo? Info(string b) => File.Exists(Ruta(b)) ? new ComplementoArchivo(Sha(Ruta(b)), new FileInfo(Ruta(b)).Length) : null;
                nuevo = new ComplementoManifiesto(version, DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                                                  string.IsNullOrWhiteSpace(p.Notas) ? null : p.Notas.Trim(), admin!.Login,
                                                  Info("64") ?? anterior?.X64, Info("32") ?? anterior?.X86);
                string tmp = RutaManifiesto + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(nuevo, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, RutaManifiesto, true);
            }
            db.Auditar(admin!.Login, "ADMIN", null, null, null, null,
                       $"Publicó el complemento versión {version} ({(hay64 ? "64" : "")}{(hay64 && hay32 ? "+" : "")}{(hay32 ? "32" : "")} bits)", ip(ctx));
            app.Logger.LogInformation("Complemento {Version} publicado por {Login}", version, admin.Login);
            return Results.Ok(new { ok = true, version, fecha = nuevo.Fecha });
        });

        // ---- Instalación con un enlace ----
        app.MapGet("/instalar", (HttpContext ctx) =>
        {
            string? url = UrlBase(ctx);
            if (url == null) return Error(400, "Dirección de servidor inválida.");
            var m = Leer();
            string comando = $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"irm '{url}/instalar/instalar.ps1' | iex\"";
            string html = Pagina
                .Replace("{{URL}}", System.Net.WebUtility.HtmlEncode(url))
                .Replace("{{COMANDO}}", System.Net.WebUtility.HtmlEncode(comando))
                .Replace("{{VERSION}}", m == null ? "todavía no hay una versión publicada"
                                                  : System.Net.WebUtility.HtmlEncode("Versión " + m.Version + " · publicada el " + m.Fecha));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/instalar/InstalarReportesPOS.cmd", (HttpContext ctx) =>
        {
            string? url = UrlBase(ctx);
            if (url == null) return Error(400, "Dirección de servidor inválida.");
            string cmd = "@echo off\r\ntitle Instalar - Reporte de Ventas POS\r\n" +
                         $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"irm '{url}/instalar/instalar.ps1' | iex\"\r\n" +
                         "echo.\r\npause\r\n";
            return Results.File(Encoding.ASCII.GetBytes(cmd), "application/octet-stream", "InstalarReportesPOS.cmd");
        });

        app.MapGet("/instalar/instalar.ps1", (HttpContext ctx) =>
        {
            string? url = UrlBase(ctx);
            if (url == null) return Error(400, "Dirección de servidor inválida.");
            return Results.Text(Script.Replace("__API__", url.Replace("'", "''")), "text/plain; charset=utf-8");
        });
    }

    // ---------------------------------------------------------------- instalador (PowerShell)
    // Solo ASCII: PowerShell 5.1 lo ejecuta sin problemas de codificación.
    private const string Script = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ApiUrl  = '__API__'
$Destino = Join-Path $env:LOCALAPPDATA 'ReportesVentasPOS'
$Log     = Join-Path $env:TEMP 'Instalar-ReportesVentasPOS.log'
function Escribir([string]$t, [string]$c = 'Gray') {
    Write-Host "  $t" -ForegroundColor $c
    Add-Content -Path $Log -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $t)
}
function Get-BitsOffice {
    $p = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration' -ErrorAction SilentlyContinue).Platform
    if ($p -eq 'x64') { return 64 }
    if ($p -eq 'x86') { return 32 }
    foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Office\16.0\Outlook', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Office\16.0\Outlook',
                   'HKLM:\SOFTWARE\Microsoft\Office\15.0\Outlook', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Office\15.0\Outlook') {
        $b = (Get-ItemProperty $k -ErrorAction SilentlyContinue).Bitness
        if ($b -eq 'x64') { return 64 }
        if ($b -eq 'x86') { return 32 }
    }
    $exe = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\excel.exe' -ErrorAction SilentlyContinue).'(default)'
    if ($exe -and $exe -like '*Program Files (x86)*') { return 32 }
    if ($exe) { return 64 }
    if ([Environment]::Is64BitOperatingSystem) { return 64 } else { return 32 }
}
function Get-ClavesExcel {
    $v = Get-ChildItem 'HKCU:\Software\Microsoft\Office' -ErrorAction SilentlyContinue |
         Where-Object { $_.PSChildName -match '^\d+\.\d+$' -and (Test-Path (Join-Path $_.PSPath 'Excel')) } |
         ForEach-Object { $_.PSChildName }
    if (-not $v) { $v = @('16.0') }
    return $v
}
try {
    Write-Host ''
    Write-Host '  ============================================================' -ForegroundColor DarkGreen
    Write-Host '    Reporte de Ventas POS  -  Instalacion desde el servidor' -ForegroundColor Green
    Write-Host '  ============================================================' -ForegroundColor DarkGreen
    Write-Host ''
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072
    Escribir "Servidor: $ApiUrl"
    $m = Invoke-RestMethod -Uri "$ApiUrl/api/complemento" -UseBasicParsing -TimeoutSec 20
    if (-not $m.disponible) { throw 'El servidor todavia no tiene el complemento publicado. Avise al administrador.' }
    $bits = Get-BitsOffice
    $info = if ($bits -eq 64) { $m.x64 } else { $m.x86 }
    if (-not $info) { throw "El servidor no tiene la version de $bits bits del complemento." }
    Escribir "Excel de $bits bits  -  complemento version $($m.version)"

    $excel = Get-Process EXCEL -ErrorAction SilentlyContinue
    if ($excel) {
        Escribir 'Excel esta abierto. Guarde sus libros antes de continuar.' 'Yellow'
        $r = Read-Host '  Cerrar Excel ahora? (S/N)'
        if ($r -notmatch '^[sS]') { throw 'Instalacion cancelada: Excel debe estar cerrado.' }
        $excel | Stop-Process -Force
        Start-Sleep -Seconds 3
    }

    New-Item -ItemType Directory -Force -Path $Destino | Out-Null
    $xll = Join-Path $Destino 'ReportesVentasPOS.xll'
    $tmp = "$xll.descarga"
    Escribir 'Descargando el complemento...'
    Invoke-WebRequest -Uri "$ApiUrl/api/complemento/descargar/$bits" -OutFile $tmp -UseBasicParsing -TimeoutSec 300
    $hash = (Get-FileHash $tmp -Algorithm SHA256).Hash
    if ($hash -ne $info.sha256) { Remove-Item $tmp -Force; throw 'La descarga llego incompleta o danada. Intente de nuevo.' }
    Move-Item $tmp $xll -Force
    Unblock-File -Path $xll -ErrorAction SilentlyContinue
    Get-ChildItem $Destino -Filter 'ReportesVentasPOS.xll.viejo*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    $url = [System.Security.SecurityElement]::Escape($ApiUrl)
    $config = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<configuration>`r`n  <appSettings>`r`n    <add key=`"ApiUrl`" value=`"$url`" />`r`n  </appSettings>`r`n</configuration>`r`n"
    [System.IO.File]::WriteAllText("$xll.config", $config, (New-Object System.Text.UTF8Encoding($true)))
    Set-Content -Path (Join-Path $Destino 'version.txt') -Value "Version: $($m.version)`r`nInstalado: $(Get-Date -Format 'yyyy-MM-dd HH:mm')`r`nAPI: $ApiUrl`r`nExcel: $bits bits"
    Escribir "Complemento copiado en $Destino" 'Green'

    foreach ($v in Get-ClavesExcel) {
        $clave = "HKCU:\Software\Microsoft\Office\$v\Excel\Options"
        if (-not (Test-Path $clave)) { New-Item -Path $clave -Force | Out-Null }
        $props = (Get-ItemProperty $clave -ErrorAction SilentlyContinue).PSObject.Properties |
                 Where-Object { $_.Name -match '^OPEN\d*$' } |
                 Sort-Object { if ($_.Name -eq 'OPEN') { 0 } else { [int]$_.Name.Substring(4) + 1 } }
        $valores = @()
        foreach ($o in $props) {
            if ($o.Value -notmatch 'ReportesVentasPOS|VentasExcelFunctions') { $valores += $o.Value }
            Remove-ItemProperty -Path $clave -Name $o.Name -ErrorAction SilentlyContinue
        }
        $valores += "/R `"$xll`""
        for ($i = 0; $i -lt $valores.Count; $i++) {
            $n = if ($i -eq 0) { 'OPEN' } else { "OPEN$i" }
            New-ItemProperty -Path $clave -Name $n -Value $valores[$i] -PropertyType String -Force | Out-Null
        }
        $am = "HKCU:\Software\Microsoft\Office\$v\Excel\Add-in Manager"
        if (Test-Path $am) {
            (Get-ItemProperty $am).PSObject.Properties | Where-Object { $_.Name -match 'ReportesVentasPOS|VentasExcelFunctions' } |
                ForEach-Object { Remove-ItemProperty -Path $am -Name $_.Name -ErrorAction SilentlyContinue }
        }
        Escribir "Registrado en Excel (Office $v)" 'Green'
    }
    Write-Host ''
    Escribir 'INSTALACION COMPLETADA. Desde ahora el complemento se actualiza solo.' 'Green'
    Write-Host '  Abra Excel: vera la pestana "Reporte Facturacion".' -ForegroundColor Gray
    $r = Read-Host '  Abrir Excel ahora? (S/N)'
    if ($r -match '^[sS]') { Start-Process 'excel.exe' -ErrorAction SilentlyContinue }
}
catch {
    Write-Host ''
    Escribir ('ERROR: ' + $_.Exception.Message) 'Red'
    Write-Host "  Registro: $Log" -ForegroundColor DarkGray
    Read-Host '  Pulse Enter para cerrar' | Out-Null
}
""";

    // ---------------------------------------------------------------- página /instalar
    private const string Pagina = """
<!doctype html>
<html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Instalar Reporte de Ventas POS</title>
<style>
 :root{--verde:#16a34a;--osc:#0f172a;--gris:#64748b;--linea:#e2e8f0}
 *{box-sizing:border-box} body{margin:0;font-family:"Segoe UI",system-ui,sans-serif;background:#f1f5f9;color:var(--osc)}
 .caja{max-width:720px;margin:40px auto;background:#fff;border-radius:16px;box-shadow:0 10px 30px rgba(15,23,42,.12);overflow:hidden}
 header{background:linear-gradient(135deg,#0b1220,#0f2a1d);color:#fff;padding:26px 30px}
 header h1{margin:0;font-size:24px;font-weight:600} header p{margin:6px 0 0;color:#86efac;font-size:14px}
 main{padding:22px 30px 28px} h2{font-size:13px;letter-spacing:.06em;color:var(--gris);margin:22px 0 8px}
 ol{margin:0;padding-left:20px;line-height:1.7} .btn{display:inline-block;background:var(--verde);color:#fff;text-decoration:none;
 padding:12px 22px;border-radius:10px;font-weight:600;margin:6px 0} .btn:hover{background:#15803d}
 pre{background:#0f172a;color:#e2e8f0;padding:14px;border-radius:10px;white-space:pre-wrap;word-break:break-all;font-size:13px;margin:6px 0}
 button{border:1px solid var(--linea);background:#fff;border-radius:8px;padding:6px 12px;cursor:pointer}
 .nota{color:var(--gris);font-size:13px;line-height:1.6}
</style></head><body>
<div class="caja">
 <header><h1>Reporte de Ventas POS</h1><p>Complemento de Excel · {{VERSION}}</p></header>
 <main>
  <h2>OPCIÓN 1 · DESCARGAR Y EJECUTAR</h2>
  <ol>
   <li>Descargue el instalador:<br><a class="btn" href="{{URL}}/instalar/InstalarReportesPOS.cmd">Descargar InstalarReportesPOS.cmd</a></li>
   <li>Ábralo con doble clic. Si Windows muestra un aviso, elija <b>Más información → Ejecutar de todas formas</b>.</li>
   <li>Responda las preguntas y abra Excel: verá la pestaña <b>Centro de Reportes</b>.</li>
  </ol>
  <h2>OPCIÓN 2 · COPIAR Y PEGAR</h2>
  <p class="nota">Pulse <b>Windows + R</b>, pegue esto y pulse Enter:</p>
  <pre id="cmd">{{COMANDO}}</pre>
  <button onclick="navigator.clipboard.writeText(document.getElementById('cmd').innerText);this.innerText='Copiado ✓'">Copiar comando</button>
  <h2>BUENO SABER</h2>
  <p class="nota">No necesita permisos de administrador. Detecta solo si su Excel es de 32 o 64 bits.
  Después de instalado, el complemento <b>se actualiza solo</b> cada vez que se publica una versión nueva.
  Servidor: {{URL}}</p>
 </main>
</div></body></html>
""";
}
