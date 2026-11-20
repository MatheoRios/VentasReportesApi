using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VentasReportesApi;

/// <summary>Contraseñas: PBKDF2-SHA256 con sal aleatoria (nunca se guarda la clave en texto).</summary>
public static class Claves
{
    private const int Iteraciones = 120_000;

    public static string Hash(string clave)
    {
        byte[] sal = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(clave, sal, Iteraciones, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iteraciones}${Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verificar(string clave, string guardado)
    {
        try
        {
            var p = guardado.Split('$');
            if (p.Length != 4 || p[0] != "pbkdf2") return false;
            int it = int.Parse(p[1]);
            byte[] sal = Convert.FromBase64String(p[2]);
            byte[] esperado = Convert.FromBase64String(p[3]);
            byte[] real = Rfc2898DeriveBytes.Pbkdf2(clave, sal, it, HashAlgorithmName.SHA256, esperado.Length);
            return CryptographicOperations.FixedTimeEquals(real, esperado);
        }
        catch { return false; }
    }

    public static string? ValidarPolitica(string clave) =>
        string.IsNullOrEmpty(clave) || clave.Length < 8 ? "La clave debe tener al menos 8 caracteres." : null;
}

/// <summary>
/// Token de sesión firmado con HMAC-SHA256. La llave se genera sola la primera vez
/// (datos\token.key). Borrar ese archivo invalida todas las sesiones abiertas.
/// </summary>
public sealed class Tokens
{
    private readonly byte[] llave;

    public Tokens(string rutaLlave)
    {
        if (File.Exists(rutaLlave))
            llave = Convert.FromBase64String(File.ReadAllText(rutaLlave).Trim());
        else
        {
            llave = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(rutaLlave, Convert.ToBase64String(llave));
            RestringirPermisosArchivo(rutaLlave);
        }
    }

    internal static void RestringirPermisosArchivo(string ruta)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    private sealed record Carga(string u, long exp, long tv = 1);

    public (string token, DateTime expira) Crear(string login, double horas, long tokenVersion = 1)
    {
        var expira = DateTime.UtcNow.AddHours(horas);
        string carga = B64(JsonSerializer.SerializeToUtf8Bytes(new Carga(login, new DateTimeOffset(expira).ToUnixTimeSeconds(), tokenVersion)));
        return ($"{carga}.{Firmar(carga)}", expira);
    }

    /// <returns>El login si el token es válido y no venció; si no, null.</returns>
    public string? Validar(string token) => ValidarConVersion(token)?.login;

    /// <returns>(login, tokenVersion) si el token es válido y no venció; si no, null.</returns>
    public (string login, long tokenVersion)? ValidarConVersion(string token)
    {
        var partes = token.Split('.');
        if (partes.Length != 2) return null;
        byte[] esperada = Encoding.ASCII.GetBytes(Firmar(partes[0]));
        byte[] recibida = Encoding.ASCII.GetBytes(partes[1]);
        if (esperada.Length != recibida.Length || !CryptographicOperations.FixedTimeEquals(esperada, recibida)) return null;
        try
        {
            var c = JsonSerializer.Deserialize<Carga>(DesdeB64(partes[0]));
            if (c == null || DateTimeOffset.FromUnixTimeSeconds(c.exp) < DateTimeOffset.UtcNow) return null;
            return (c.u, c.tv <= 0 ? 1 : c.tv);
        }
        catch { return null; }
    }

    private string Firmar(string datos)
    {
        using var h = new HMACSHA256(llave);
        return B64(h.ComputeHash(Encoding.UTF8.GetBytes(datos)));
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DesdeB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
