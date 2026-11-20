using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace VentasReportesApi;

/// <summary>Conexión a una tienda (la clave SQL se guarda cifrada).</summary>
public sealed class Tienda
{
    public long Id { get; init; }
    public string Nombre { get; init; } = "";
    public string Servidor { get; init; } = "";
    public int? Puerto { get; init; }
    public string BaseDatos { get; init; } = "";
    public string Usuario { get; init; } = "";
    public string ClaveCifrada { get; init; } = "";
    public int TimeoutSeg { get; init; } = 30;
    public bool Activa { get; init; } = true;
    public string? Modificado { get; init; }
}

/// <summary>
/// Cifra las claves SQL de las tiendas con AES-GCM. La llave vive en datos\tiendas.key
/// (se crea sola). Sin ese archivo no se pueden leer las claves: respáldelo junto con la base.
/// </summary>
public sealed class Cifrado
{
    private readonly byte[] llave;

    public Cifrado(string ruta)
    {
        if (File.Exists(ruta)) llave = Convert.FromBase64String(File.ReadAllText(ruta).Trim());
        else
        {
            llave = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(ruta, Convert.ToBase64String(llave));
            Tokens.RestringirPermisosArchivo(ruta);
        }
    }

    public string Cifrar(string texto)
    {
        byte[] plano = Encoding.UTF8.GetBytes(texto);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] cifrado = new byte[plano.Length];
        using (var aes = new AesGcm(llave, 16)) aes.Encrypt(nonce, plano, cifrado, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cifrado).ToArray());
    }

    public string Descifrar(string base64)
    {
        byte[] todo = Convert.FromBase64String(base64);
        byte[] nonce = todo[..12], tag = todo[12..28], cifrado = todo[28..];
        byte[] plano = new byte[cifrado.Length];
        using (var aes = new AesGcm(llave, 16)) aes.Decrypt(nonce, cifrado, tag, plano);
        return Encoding.UTF8.GetString(plano);
    }
}

public static class Conexiones
{
    // Valor seguro dentro de una cadena de conexión (comillas si trae ; = ' ")
    private static string V(string s) =>
        s.IndexOfAny(new[] { ';', '=', '\'', '"' }) >= 0 || s.Trim() != s ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string Cadena(string servidor, int? puerto, string baseDatos, string usuario, string clave,
                                int timeoutSeg, int reintentos = 3)
    {
        string server = puerto is > 0 ? $"{servidor},{puerto}" : servidor;
        return $"Server={V(server)};Database={V(baseDatos)};User Id={V(usuario)};Password={V(clave)};" +
               $"Encrypt=False;TrustServerCertificate=True;Connect Timeout={timeoutSeg};" +
               $"ConnectRetryCount={reintentos};ConnectRetryInterval=10;Application Name=VentasReportesApi;";
    }

    /// <summary>Importa sucursales.json (formato anterior) a la base, una sola vez.</summary>
    public static int ImportarJson(string rutaJson, Almacen db, Cifrado cifrado)
    {
        if (!File.Exists(rutaJson) || db.ContarTiendas() > 0) return 0;
        var cfg = new ConfigurationBuilder().AddJsonFile(rutaJson, optional: true).Build();
        int n = 0;
        foreach (var c in cfg.GetSection("Sucursales").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(c.Value)) continue;
            var b = new DbConnectionStringBuilder { ConnectionString = c.Value };
            string Leer(params string[] claves)
            {
                foreach (var k in claves) if (b.TryGetValue(k, out var v) && v != null) return v.ToString()!;
                return "";
            }
            string server = Leer("server", "data source", "address", "addr");
            int? puerto = null;
            int coma = server.LastIndexOf(',');
            if (coma > 0 && int.TryParse(server[(coma + 1)..].Trim(), out var p)) { puerto = p; server = server[..coma].Trim(); }
            int timeout = int.TryParse(Leer("connect timeout", "connection timeout", "timeout"), out var t) ? t : 30;

            db.CrearTienda(new Tienda
            {
                Nombre = c.Key.Trim(),
                Servidor = server,
                Puerto = puerto,
                BaseDatos = Leer("database", "initial catalog"),
                Usuario = Leer("user id", "uid", "user"),
                TimeoutSeg = timeout,
                Activa = true
            }, cifrado.Cifrar(Leer("password", "pwd")));
            n++;
        }
        if (n > 0) File.Move(rutaJson, rutaJson + ".importado", overwrite: true);
        return n;
    }

    /// <summary>Prueba la conexión y verifica que exista el procedimiento del reporte.</summary>
    public static async Task<object> ProbarAsync(string cadena, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(cadena);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)), " +
                "CASE WHEN OBJECT_ID('dbo.getVentasPosStore') IS NULL THEN 0 ELSE 1 END, DB_NAME()", conn);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            string version = rd.GetString(0);
            bool proc = rd.GetInt32(1) == 1;
            string baseDatos = rd.GetString(2);
            sw.Stop();
            return new
            {
                ok = true,
                procedimiento = proc,
                ms = sw.ElapsedMilliseconds,
                mensaje = proc
                    ? $"Conectado en {sw.ElapsedMilliseconds:N0} ms · SQL Server {version} · base {baseDatos} · procedimiento del reporte encontrado."
                    : $"Conectado en {sw.ElapsedMilliseconds:N0} ms a la base {baseDatos}, pero NO existe dbo.getVentasPosStore: el reporte fallará en esta tienda."
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, procedimiento = false, ms = sw.ElapsedMilliseconds, mensaje = ConsultaSucursal.MensajeAmigable(ex) + "  (" + Corto(ex.Message) + ")" };
        }
    }

    private static string Corto(string s) => s.Length > 160 ? s[..160] + "…" : s;
}

// ---------------------------------------------------------------------- tabla Tiendas
public sealed partial class Almacen
{
    public void InicializarTiendas()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Tiendas (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    Nombre       TEXT NOT NULL UNIQUE COLLATE NOCASE,
    Servidor     TEXT NOT NULL,
    Puerto       INTEGER NULL,
    BaseDatos    TEXT NOT NULL,
    Usuario      TEXT NOT NULL,
    ClaveCifrada TEXT NOT NULL,
    TimeoutSeg   INTEGER NOT NULL DEFAULT 30,
    Activa       INTEGER NOT NULL DEFAULT 1,
    Modificado   TEXT NOT NULL
);";
        cmd.ExecuteNonQuery();
    }

    public int ContarTiendas()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Tiendas";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private const string ColsTienda = "Id, Nombre, Servidor, Puerto, BaseDatos, Usuario, ClaveCifrada, TimeoutSeg, Activa, Modificado";

    private static Tienda LeerTienda(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Nombre = r.GetString(1),
        Servidor = r.GetString(2),
        Puerto = r.IsDBNull(3) ? null : (int)r.GetInt64(3),
        BaseDatos = r.GetString(4),
        Usuario = r.GetString(5),
        ClaveCifrada = r.GetString(6),
        TimeoutSeg = (int)r.GetInt64(7),
        Activa = r.GetInt64(8) == 1,
        Modificado = r.GetString(9)
    };

    public List<Tienda> ListarTiendas()
    {
        var lista = new List<Tienda>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ColsTienda} FROM Tiendas ORDER BY Nombre";
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add(LeerTienda(r));
        return lista;
    }

    public Tienda? ObtenerTienda(string? nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return null;
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ColsTienda} FROM Tiendas WHERE Nombre = $n";
        cmd.Parameters.AddWithValue("$n", nombre.Trim());
        using var r = cmd.ExecuteReader();
        return r.Read() ? LeerTienda(r) : null;
    }

    public Dictionary<string, int> UsuariosPorTienda()
    {
        var d = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Sucursal, COUNT(*) FROM UsuarioSucursal GROUP BY Sucursal";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string k = r.GetString(0);
            int cnt = (int)r.GetInt64(1);
            d[k] = d.TryGetValue(k, out var prev) ? prev + cnt : cnt;
        }
        return d;
    }

    public void CrearTienda(Tienda t, string claveCifrada)
    {
        Ejecutar(@"INSERT INTO Tiendas (Nombre, Servidor, Puerto, BaseDatos, Usuario, ClaveCifrada, TimeoutSeg, Activa, Modificado)
                   VALUES ($n, $s, $p, $b, $u, $c, $t, $a, $m)",
            ("$n", t.Nombre), ("$s", t.Servidor), ("$p", t.Puerto), ("$b", t.BaseDatos), ("$u", t.Usuario),
            ("$c", claveCifrada), ("$t", t.TimeoutSeg), ("$a", t.Activa ? 1 : 0), ("$m", Ahora()));
    }

    /// <summary>Actualiza la tienda; si cambia de nombre, también sus asignaciones a usuarios.</summary>
    public void ActualizarTienda(string nombreOriginal, Tienda t, string? claveCifradaNueva)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE Tiendas SET Nombre = $n, Servidor = $s, Puerto = $p, BaseDatos = $b, Usuario = $u,
                                ClaveCifrada = COALESCE($c, ClaveCifrada), TimeoutSeg = $t, Activa = $a, Modificado = $m
                                WHERE Nombre = $o";
            cmd.Parameters.AddWithValue("$n", t.Nombre);
            cmd.Parameters.AddWithValue("$s", t.Servidor);
            cmd.Parameters.AddWithValue("$p", (object?)t.Puerto ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$b", t.BaseDatos);
            cmd.Parameters.AddWithValue("$u", t.Usuario);
            cmd.Parameters.AddWithValue("$c", (object?)claveCifradaNueva ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", t.TimeoutSeg);
            cmd.Parameters.AddWithValue("$a", t.Activa ? 1 : 0);
            cmd.Parameters.AddWithValue("$m", Ahora());
            cmd.Parameters.AddWithValue("$o", nombreOriginal);
            cmd.ExecuteNonQuery();
        }
        if (!string.Equals(nombreOriginal, t.Nombre, StringComparison.Ordinal))
        {
            using var ren = c.CreateCommand();
            ren.Transaction = tx;
            ren.CommandText = "UPDATE UsuarioSucursal SET Sucursal = $n WHERE Sucursal = $o COLLATE NOCASE";
            ren.Parameters.AddWithValue("$n", t.Nombre);
            ren.Parameters.AddWithValue("$o", nombreOriginal);
            ren.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void EliminarTienda(string nombre)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        using (var delAsig = c.CreateCommand())
        {
            delAsig.Transaction = tx;
            delAsig.CommandText = "DELETE FROM UsuarioSucursal WHERE Sucursal = $n COLLATE NOCASE";
            delAsig.Parameters.AddWithValue("$n", nombre);
            delAsig.ExecuteNonQuery();
        }
        using (var delTienda = c.CreateCommand())
        {
            delTienda.Transaction = tx;
            delTienda.CommandText = "DELETE FROM Tiendas WHERE Nombre = $n";
            delTienda.Parameters.AddWithValue("$n", nombre);
            delTienda.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
