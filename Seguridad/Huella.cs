using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VentasReportesApi;

// =====================================================================================
//  Ingreso con huella / Face ID (WebAuthn, llaves de acceso del teléfono)
//  - El teléfono guarda una llave privada que nunca sale del equipo; la API guarda solo la llave pública.
//  - Para usarla, el usuario primero ingresa con su clave y activa la huella en "Más".
//  - Solo funciona por HTTPS y en la dirección configurada en Reportes:OrigenWeb (por ejemplo la de Tailscale).
//  - Si el administrador cambia la clave del usuario, se borran sus huellas (hay que volver a activarlas).
// =====================================================================================

public sealed record HuellaCredencial(string Id, long UsuarioId, string Login, string LlavePublica, int Alg, long Contador,
                                      string Nombre, string Creado, string? UltimoUso);

public sealed partial class Almacen
{
    public void InicializarHuellas()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Huellas (
    Id          TEXT PRIMARY KEY,
    UsuarioId   INTEGER NOT NULL REFERENCES Usuarios(Id) ON DELETE CASCADE,
    LlavePublica TEXT NOT NULL,
    Alg         INTEGER NOT NULL,
    Contador    INTEGER NOT NULL DEFAULT 0,
    Nombre      TEXT NOT NULL DEFAULT '',
    Creado      TEXT NOT NULL,
    UltimoUso   TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Huellas_Usuario ON Huellas(UsuarioId);";
        cmd.ExecuteNonQuery();
    }

    public List<HuellaCredencial> HuellasDe(long usuarioId) => LeerHuellas("WHERE H.UsuarioId = $u", ("$u", usuarioId));
    public HuellaCredencial? Huella(string id) => LeerHuellas("WHERE H.Id = $i", ("$i", id)).FirstOrDefault();

    private List<HuellaCredencial> LeerHuellas(string where, params (string, object)[] p)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT H.Id, H.UsuarioId, U.Login, H.LlavePublica, H.Alg, H.Contador, H.Nombre, H.Creado, H.UltimoUso " +
                          "FROM Huellas H JOIN Usuarios U ON U.Id = H.UsuarioId " + where + " ORDER BY H.Creado";
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        var l = new List<HuellaCredencial>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            l.Add(new HuellaCredencial(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), (int)r.GetInt64(4), r.GetInt64(5),
                                       r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return l;
    }

    public void GuardarHuella(string id, long usuarioId, string llave, int alg, long contador, string nombre) =>
        Ejecutar("INSERT OR REPLACE INTO Huellas (Id, UsuarioId, LlavePublica, Alg, Contador, Nombre, Creado) VALUES ($i, $u, $k, $a, $c, $n, $f)",
                 ("$i", id), ("$u", usuarioId), ("$k", llave), ("$a", alg), ("$c", contador), ("$n", nombre), ("$f", Ahora()));

    public void UsoHuella(string id, long contador) =>
        Ejecutar("UPDATE Huellas SET Contador = $c, UltimoUso = $f WHERE Id = $i", ("$i", id), ("$c", contador), ("$f", Ahora()));

    public bool QuitarHuella(string id, long usuarioId) =>
        Ejecutar("DELETE FROM Huellas WHERE Id = $i AND UsuarioId = $u", ("$i", id), ("$u", usuarioId)) > 0;

    /// <summary>Quita el bloqueo por intentos fallidos (lo usa el panel de administración del teléfono).</summary>
    public void Desbloquear(long usuarioId) =>
        Ejecutar("UPDATE Usuarios SET Fallos = 0, BloqueadoHasta = NULL WHERE Id = $id", ("$id", usuarioId));

    public int QuitarHuellasDe(string login) =>
        Ejecutar("DELETE FROM Huellas WHERE UsuarioId = (SELECT Id FROM Usuarios WHERE Login = $l)", ("$l", login));
}

public sealed record HuellaRegistroPeticion(string? Id, string? ClientDataJSON, string? AuthenticatorData, string? PublicKey, int Alg, string? Nombre);
public sealed record HuellaOpcionesPeticion(string? Usuario);
public sealed record HuellaIngresoPeticion(string? Usuario, string? Id, string? ClientDataJSON, string? AuthenticatorData, string? Signature);
public sealed record HuellaQuitarPeticion(string? Id);

public static class HuellaApi
{
    private sealed record Reto(string Tipo, string Login, DateTime Vence);
    private static readonly ConcurrentDictionary<string, Reto> retos = new();
    private const int MaxRetos = 2000;

    public static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] DeB64u(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static string NuevoReto(string tipo, string login)
    {
        var ahora = DateTime.UtcNow;
        if (retos.Count > MaxRetos) foreach (var kv in retos) if (kv.Value.Vence < ahora) retos.TryRemove(kv.Key, out _);
        if (retos.Count > MaxRetos) throw new InvalidOperationException("Demasiadas solicitudes de huella. Intente en un minuto.");
        string r = B64u(RandomNumberGenerator.GetBytes(32));
        retos[r] = new Reto(tipo, login, ahora.AddMinutes(3));
        return r;
    }

    /// <summary>Datos del autenticador: hash del sitio (32) · banderas (1) · contador (4) · …</summary>
    private sealed record DatosAut(byte[] RpIdHash, byte Banderas, long Contador, byte[] Crudo);
    private static DatosAut LeerDatos(byte[] a)
    {
        if (a.Length < 37) throw new InvalidOperationException("Datos del autenticador incompletos.");
        long cont = ((long)a[33] << 24) | ((long)a[34] << 16) | ((long)a[35] << 8) | a[36];
        return new DatosAut(a[..32], a[32], cont, a);
    }

    /// <summary>Valida clientDataJSON (tipo, reto de un solo uso, origen) y devuelve el reto usado.</summary>
    private static Reto ValidarCliente(byte[] clientData, string tipoEsperado, string origen)
    {
        using var j = JsonDocument.Parse(clientData);
        var raiz = j.RootElement;
        string tipo = raiz.GetProperty("type").GetString() ?? "";
        string reto = raiz.GetProperty("challenge").GetString() ?? "";
        string org = raiz.GetProperty("origin").GetString() ?? "";
        if (tipo != tipoEsperado) throw new InvalidOperationException("Respuesta de huella inválida (tipo).");
        if (!string.Equals(org.TrimEnd('/'), origen, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"La huella solo funciona en {origen}.");
        if (!retos.TryRemove(reto, out var r) || r.Vence < DateTime.UtcNow || r.Tipo != tipoEsperado)
            throw new InvalidOperationException("La solicitud de huella venció. Intente de nuevo.");
        return r;
    }

    private static void ValidarDatos(DatosAut d, string rpId)
    {
        if (!CryptographicOperations.FixedTimeEquals(d.RpIdHash, SHA256.HashData(Encoding.UTF8.GetBytes(rpId))))
            throw new InvalidOperationException("La huella es de otro sitio.");
        if ((d.Banderas & 0x01) == 0) throw new InvalidOperationException("No se confirmó la presencia del usuario.");
        if ((d.Banderas & 0x04) == 0) throw new InvalidOperationException("El teléfono no verificó la huella / Face ID.");
    }

    private static bool VerificarFirma(int alg, byte[] spki, byte[] datos, byte[] firma)
    {
        if (alg == -7)
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(spki, out _);
            return ec.VerifyData(datos, firma, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        if (alg == -257)
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out _);
            return rsa.VerifyData(datos, firma, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        return false;
    }

    public static void Mapear(WebApplication app, Almacen almacen, Tokens tokens, double horasSesion,
                              Func<HttpContext, Usuario?> autenticar, Func<HttpContext, string> ip, string? origenWeb)
    {
        almacen.InicializarHuellas();
        string origen = (origenWeb ?? "").Trim().TrimEnd('/');
        string rpId = Uri.TryCreate(origen, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri.Host : "";
        bool activa = rpId.Length > 0;
        if (!activa && origen.Length > 0) app.Logger.LogWarning("Reportes:OrigenWeb debe ser una dirección https://… ({Origen}); la huella queda desactivada.", origen);

        IResult Error(int c, string m) => Results.Json(new { error = m }, statusCode: c);
        const string ClaveTemporal = "Su clave es temporal: debe cambiarla antes de continuar.";
        var g = app.MapGroup("/api/huella");

        g.MapGet("/estado", () => Results.Ok(new { disponible = activa, origen = activa ? origen : null, rpId = activa ? rpId : null }));

        // ---- activar en este teléfono (con sesión iniciada) ----
        g.MapPost("/registro/opciones", (HttpContext ctx) =>
        {
            if (!activa) return Error(503, "El ingreso con huella no está configurado en el servidor.");
            var u = autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            string reto;
            try { reto = NuevoReto("webauthn.create", u.Login); } catch (InvalidOperationException ex) { return Error(429, ex.Message); }
            return Results.Ok(new
            {
                challenge = reto,
                rp = new { id = rpId, name = "Reportes POS" },
                user = new { id = B64u(SHA256.HashData(Encoding.UTF8.GetBytes("rpos:" + u.Login.ToLowerInvariant()))), name = u.Login, displayName = u.Nombre },
                pubKeyCredParams = new[] { new { type = "public-key", alg = -7 }, new { type = "public-key", alg = -257 } },
                authenticatorSelection = new { authenticatorAttachment = "platform", userVerification = "required", residentKey = "preferred" },
                timeout = 60000,
                attestation = "none",
                excludeCredentials = almacen.HuellasDe(u.Id).Select(h => new { type = "public-key", id = h.Id })
            });
        });

        g.MapPost("/registro", (HuellaRegistroPeticion p, HttpContext ctx) =>
        {
            if (!activa) return Error(503, "El ingreso con huella no está configurado en el servidor.");
            var u = autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            try
            {
                if (string.IsNullOrEmpty(p.Id) || p.ClientDataJSON == null || p.AuthenticatorData == null || p.PublicKey == null)
                    throw new InvalidOperationException("Faltan datos de la huella.");
                if (p.Alg != -7 && p.Alg != -257) throw new InvalidOperationException("El teléfono usa un tipo de llave que no se admite.");
                var reto = ValidarCliente(DeB64u(p.ClientDataJSON), "webauthn.create", origen);
                if (!reto.Login.Equals(u.Login, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("La solicitud no es de este usuario.");
                var d = LeerDatos(DeB64u(p.AuthenticatorData));
                ValidarDatos(d, rpId);
                // la llave debe poder leerse (se prueba con una firma vacía)
                byte[] spki = DeB64u(p.PublicKey);
                if (p.Alg == -7) { using var ec = ECDsa.Create(); ec.ImportSubjectPublicKeyInfo(spki, out _); }
                else { using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(spki, out _); }
                if (p.Id.Length > 400) throw new InvalidOperationException("Identificador de huella inválido.");
                string nombre = (p.Nombre ?? "").Trim();
                if (nombre.Length > 60) nombre = nombre[..60];
                almacen.GuardarHuella(p.Id, u.Id, Convert.ToBase64String(spki), p.Alg, d.Contador, nombre.Length > 0 ? nombre : "Teléfono");
                almacen.Auditar(u.Login, "HUELLA", resultado: "Activó el ingreso con huella en " + (nombre.Length > 0 ? nombre : "un teléfono"), ip: ip(ctx));
                return Results.Ok(new { ok = true, id = p.Id });
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or CryptographicException or JsonException or KeyNotFoundException)
            {
                almacen.Auditar(u.Login, "HUELLA", resultado: "No se pudo activar: " + ex.Message, ip: ip(ctx));
                return Error(400, ex is InvalidOperationException ? ex.Message : "No se pudo leer la huella del teléfono.");
            }
        });

        g.MapGet("/equipos", (HttpContext ctx) =>
        {
            var u = autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            return Results.Ok(new { disponible = activa, equipos = almacen.HuellasDe(u.Id).Select(h => new { id = h.Id, nombre = h.Nombre, creado = h.Creado, ultimoUso = h.UltimoUso }) });
        });

        g.MapPost("/quitar", (HuellaQuitarPeticion p, HttpContext ctx) =>
        {
            var u = autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            if (string.IsNullOrEmpty(p.Id) || !almacen.QuitarHuella(p.Id, u.Id)) return Error(404, "Ese equipo no está registrado.");
            almacen.Auditar(u.Login, "HUELLA", resultado: "Quitó una huella", ip: ip(ctx));
            return Results.Ok(new { ok = true });
        });

        // ---- ingresar con huella (sin sesión) ----
        g.MapPost("/ingreso/opciones", (HuellaOpcionesPeticion p) =>
        {
            if (!activa) return Error(503, "El ingreso con huella no está configurado en el servidor.");
            string login = (p.Usuario ?? "").Trim();
            var u = login.Length == 0 ? null : almacen.ObtenerUsuario(login);
            string reto;
            try { reto = NuevoReto("webauthn.get", u?.Login ?? ""); } catch (InvalidOperationException ex) { return Error(429, ex.Message); }
            // si el usuario no existe se responde igual (no se revela qué usuarios existen)
            var lista = u == null ? new List<HuellaCredencial>() : almacen.HuellasDe(u.Id);
            return Results.Ok(new
            {
                challenge = reto, rpId, timeout = 60000, userVerification = "required",
                allowCredentials = lista.Select(h => new { type = "public-key", id = h.Id, transports = new[] { "internal", "hybrid" } })
            });
        });

        g.MapPost("/ingreso", (HuellaIngresoPeticion p, HttpContext ctx) =>
        {
            if (!activa) return Error(503, "El ingreso con huella no está configurado en el servidor.");
            string quien = (p.Usuario ?? "").Trim();
            try
            {
                if (string.IsNullOrEmpty(p.Id) || p.ClientDataJSON == null || p.AuthenticatorData == null || p.Signature == null)
                    throw new InvalidOperationException("Faltan datos de la huella.");
                byte[] cd = DeB64u(p.ClientDataJSON), ad = DeB64u(p.AuthenticatorData), firma = DeB64u(p.Signature);
                var reto = ValidarCliente(cd, "webauthn.get", origen);
                var h = almacen.Huella(p.Id) ?? throw new InvalidOperationException("Este teléfono ya no tiene la huella activa. Ingrese con su clave y actívela de nuevo.");
                if (reto.Login.Length > 0 && !reto.Login.Equals(h.Login, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("La huella no corresponde a ese usuario.");
                var d = LeerDatos(ad);
                ValidarDatos(d, rpId);
                byte[] datos = ad.Concat(SHA256.HashData(cd)).ToArray();
                if (!VerificarFirma(h.Alg, Convert.FromBase64String(h.LlavePublica), datos, firma))
                    throw new InvalidOperationException("La huella no es válida.");
                // contador: si el teléfono lo usa, debe subir siempre (si baja, la llave pudo haberse copiado)
                if ((h.Contador > 0 || d.Contador > 0) && d.Contador <= h.Contador)
                    throw new InvalidOperationException("La huella no es válida (contador). Actívela de nuevo en este teléfono.");

                var u = almacen.ObtenerUsuario(h.Login);
                if (u == null || !u.Activo) throw new InvalidOperationException("Usuario inactivo.");
                if (u.BloqueadoHasta > DateTime.Now) throw new InvalidOperationException($"Usuario bloqueado por intentos fallidos. Intente después de las {u.BloqueadoHasta:HH:mm}.");
                almacen.UsoHuella(h.Id, d.Contador);
                almacen.LoginExitoso(u.Id);
                var (token, expira) = tokens.Crear(u.Login, horasSesion, u.TokenVersion);
                almacen.Auditar(u.Login, "LOGIN", resultado: "OK (huella" + (h.Nombre.Length > 0 ? ": " + h.Nombre : "") + ")", ip: ip(ctx));
                return Results.Ok(new { token, usuario = u.Login, nombre = u.Nombre, rol = u.Rol, expira = expira.ToLocalTime(),
                                        debeCambiarClave = u.DebeCambiarClave, permisos = u.ListaPermisos });
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or CryptographicException or JsonException or KeyNotFoundException)
            {
                almacen.Auditar(quien, "LOGIN", resultado: "Huella rechazada: " + ex.Message, ip: ip(ctx));
                return Error(401, ex is InvalidOperationException ? ex.Message : "No se pudo verificar la huella.");
            }
        });
    }
}
