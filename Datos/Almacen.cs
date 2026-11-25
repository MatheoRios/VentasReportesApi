using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VentasReportesApi;

public sealed class Usuario
{
    public long Id { get; init; }
    public string Login { get; init; } = "";
    public string Nombre { get; init; } = "";
    public string Rol { get; init; } = "TIENDA";      // ADMIN | REGIONAL | TIENDA
    public string HashClave { get; init; } = "";
    public bool Activo { get; init; }
    public int Fallos { get; init; }
    public DateTime? BloqueadoHasta { get; init; }
    public DateTime? UltimoAcceso { get; init; }
    public bool DebeCambiarClave { get; init; }
    public long TokenVersion { get; init; } = 1;
    /// <summary>Permisos de reportes separados por coma (null = todos, para usuarios anteriores).</summary>
    public string? Permisos { get; init; }
    public bool EsAdmin => Rol.Equals("ADMIN", StringComparison.OrdinalIgnoreCase);
    public bool EsJefe => Rol.Equals("JEFE", StringComparison.OrdinalIgnoreCase);
    /// <summary>ADMIN y JEFE ven todas las tiendas y pueden gestionar usuarios.</summary>
    public bool VeTodas => EsAdmin || EsJefe;
    public bool GestionaUsuarios => EsAdmin || EsJefe;
    /// <summary>¿Este usuario puede modificar a 'otro'? El JEFE solo maneja TIENDA y REGIONAL.</summary>
    public bool PuedeEditar(Usuario otro) => EsAdmin || (EsJefe && Almacen.RolesDeJefe.Contains(otro.Rol.ToUpperInvariant()));
    /// <summary>Permisos efectivos: el ADMIN siempre tiene todos.</summary>
    public List<string> ListaPermisos => EsAdmin ? Almacen.PermisosReporte.ToList()
        : Permisos == null ? Almacen.PermisosPorDefecto.ToList()
        : Almacen.NormalizarPermisos(Permisos.Split(','));
    public bool Tiene(string permiso) => ListaPermisos.Contains(permiso);
}

/// <summary>Usuarios, tiendas asignadas y auditoría en un archivo SQLite (datos\seguridad.db).</summary>
public sealed partial class Almacen
{
    public static readonly string[] Roles = { "ADMIN", "JEFE", "REGIONAL", "TIENDA" };
    public static readonly string[] RolesDeJefe = { "REGIONAL", "TIENDA" };   // roles que un JEFE puede asignar
    /// <summary>Reportes / módulos que se pueden permitir a cada usuario.</summary>
    public static readonly string[] PermisosReporte = { "ventas", "resumen", "categorias", "articulos", "articulos_montos", "productos", "diario", "cierres", "listado", "compras", "consolidar", "chat", "impuestos", "entradas" };
    /// <summary>Lo que recibe un usuario sin permisos guardados o creado por un Jefe: todo menos lo sensible (costos de compra, impuestos y montos en Resumen por artículo).</summary>
    public static readonly string[] PermisosPorDefecto = PermisosReporte.Where(p => p != "compras" && p != "impuestos" && p != "articulos_montos").ToArray();   // costos, impuestos y montos en artículos: solo a quien se le asigna
    public static List<string> NormalizarPermisos(IEnumerable<string>? lista) =>
        (lista ?? Array.Empty<string>()).Select(p => (p ?? "").Trim().ToLowerInvariant())
            .Where(p => PermisosReporte.Contains(p)).Distinct().OrderBy(p => Array.IndexOf(PermisosReporte, p)).ToList();
    private readonly string cadena;

    public Almacen(string rutaDb)
    {
        cadena = new SqliteConnectionStringBuilder { DataSource = rutaDb }.ToString();
    }

    private SqliteConnection Abrir()
    {
        var c = new SqliteConnection(cadena);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private static string Ahora() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public void Inicializar()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode = WAL;
CREATE TABLE IF NOT EXISTS Usuarios (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    Login          TEXT NOT NULL UNIQUE COLLATE NOCASE,
    Nombre         TEXT NOT NULL,
    Rol            TEXT NOT NULL,
    HashClave      TEXT NOT NULL,
    Activo         INTEGER NOT NULL DEFAULT 1,
    Fallos         INTEGER NOT NULL DEFAULT 0,
    BloqueadoHasta TEXT NULL,
    UltimoAcceso   TEXT NULL,
    Creado         TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS UsuarioSucursal (
    UsuarioId INTEGER NOT NULL REFERENCES Usuarios(Id) ON DELETE CASCADE,
    Sucursal  TEXT NOT NULL,
    PRIMARY KEY (UsuarioId, Sucursal)
);
CREATE TABLE IF NOT EXISTS Auditoria (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Fecha     TEXT NOT NULL,
    Login     TEXT NULL,
    Accion    TEXT NOT NULL,
    Sucursal  TEXT NULL,
    Desde     TEXT NULL,
    Hasta     TEXT NULL,
    Filas     INTEGER NULL,
    Resultado TEXT NULL,
    Ip        TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Auditoria_Fecha ON Auditoria(Fecha);";
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ usuarios
    public int ContarUsuarios()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Usuarios";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public Usuario? ObtenerUsuario(string? login)
    {
        if (string.IsNullOrWhiteSpace(login)) return null;
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Login, Nombre, Rol, HashClave, Activo, Fallos, BloqueadoHasta, UltimoAcceso, DebeCambiarClave, Permisos, TokenVersion FROM Usuarios WHERE Login = $l";
        cmd.Parameters.AddWithValue("$l", login.Trim());
        using var r = cmd.ExecuteReader();
        return r.Read() ? Leer(r) : null;
    }

    public List<Usuario> ListarUsuarios()
    {
        var lista = new List<Usuario>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Login, Nombre, Rol, HashClave, Activo, Fallos, BloqueadoHasta, UltimoAcceso, DebeCambiarClave, Permisos, TokenVersion FROM Usuarios ORDER BY Login";
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add(Leer(r));
        return lista;
    }

    private static Usuario Leer(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Login = r.GetString(1),
        Nombre = r.GetString(2),
        Rol = r.GetString(3),
        HashClave = r.GetString(4),
        Activo = r.GetInt64(5) == 1,
        Fallos = (int)r.GetInt64(6),
        BloqueadoHasta = r.IsDBNull(7) ? null : DateTime.Parse(r.GetString(7), CultureInfo.InvariantCulture),
        UltimoAcceso = r.IsDBNull(8) ? null : DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture),
        DebeCambiarClave = r.GetInt64(9) == 1,
        Permisos = r.IsDBNull(10) ? null : r.GetString(10),
        TokenVersion = r.IsDBNull(11) ? 1 : r.GetInt64(11)
    };

    public bool AsignarPermisos(long id, IEnumerable<string> permisos) =>
        Ejecutar("UPDATE Usuarios SET Permisos = $p WHERE Id = $id", ("$p", string.Join(",", NormalizarPermisos(permisos))), ("$id", id)) > 0;

    public void CrearUsuario(string login, string nombre, string rol, string clave)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Usuarios (Login, Nombre, Rol, HashClave, Creado) VALUES ($l, $n, $r, $h, $c)";
        cmd.Parameters.AddWithValue("$l", login.Trim());
        cmd.Parameters.AddWithValue("$n", nombre.Trim());
        cmd.Parameters.AddWithValue("$r", rol.ToUpperInvariant());
        cmd.Parameters.AddWithValue("$h", Claves.Hash(clave));
        cmd.Parameters.AddWithValue("$c", Ahora());
        cmd.ExecuteNonQuery();
    }

    private int Ejecutar(string sql, params (string, object?)[] p)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public bool CambiarClave(string login, string clave) =>
        Ejecutar("UPDATE Usuarios SET HashClave = $h, Fallos = 0, BloqueadoHasta = NULL, TokenVersion = TokenVersion + 1 WHERE Login = $l",
                 ("$h", Claves.Hash(clave)), ("$l", login)) > 0;

    public bool RevocarSesiones(string login) =>
        Ejecutar("UPDATE Usuarios SET TokenVersion = TokenVersion + 1 WHERE Login = $l", ("$l", login)) > 0;

    public bool CambiarRol(string login, string rol) =>
        Ejecutar("UPDATE Usuarios SET Rol = $r WHERE Login = $l", ("$r", rol.ToUpperInvariant()), ("$l", login)) > 0;

    public bool Activar(string login, bool activo) =>
        Ejecutar(activo
            ? "UPDATE Usuarios SET Activo = 1, Fallos = 0, BloqueadoHasta = NULL WHERE Login = $l"
            : "UPDATE Usuarios SET Activo = 0, Fallos = 0, BloqueadoHasta = NULL, TokenVersion = TokenVersion + 1 WHERE Login = $l",
            ("$l", login)) > 0;

    public bool ActualizarUsuario(long id, string nombre, string rol, bool activo) =>
        Ejecutar(activo
            ? "UPDATE Usuarios SET Nombre = $n, Rol = $r, Activo = 1, Fallos = 0, BloqueadoHasta = NULL WHERE Id = $id"
            : "UPDATE Usuarios SET Nombre = $n, Rol = $r, Activo = 0, Fallos = 0, BloqueadoHasta = NULL, TokenVersion = TokenVersion + 1 WHERE Id = $id",
            ("$n", nombre), ("$r", rol.ToUpperInvariant()), ("$id", id)) > 0;

    public int ContarAdminsActivos(long exceptoId)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Usuarios WHERE Rol = 'ADMIN' AND Activo = 1 AND Id <> $id";
        cmd.Parameters.AddWithValue("$id", exceptoId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool Eliminar(string login) => Ejecutar("DELETE FROM Usuarios WHERE Login = $l", ("$l", login)) > 0;

    public void RegistrarFallo(long id, int intentos, int minutos)
    {
        Ejecutar(@"UPDATE Usuarios SET Fallos = Fallos + 1,
                   BloqueadoHasta = CASE WHEN Fallos + 1 >= $i THEN $b ELSE BloqueadoHasta END
                   WHERE Id = $id",
                 ("$i", intentos),
                 ("$b", DateTime.Now.AddMinutes(minutos).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                 ("$id", id));
    }

    public void LoginExitoso(long id) =>
        Ejecutar("UPDATE Usuarios SET Fallos = 0, BloqueadoHasta = NULL, UltimoAcceso = $a WHERE Id = $id", ("$a", Ahora()), ("$id", id));

    // ------------------------------------------------------------------ tiendas
    public List<string> SucursalesDe(long usuarioId)
    {
        var lista = new List<string>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Sucursal FROM UsuarioSucursal WHERE UsuarioId = $id ORDER BY Sucursal";
        cmd.Parameters.AddWithValue("$id", usuarioId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add(r.GetString(0));
        return lista;
    }

    public void AsignarSucursales(long usuarioId, IEnumerable<string> sucursales, bool reemplazar)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        if (reemplazar)
        {
            using var del = c.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM UsuarioSucursal WHERE UsuarioId = $id";
            del.Parameters.AddWithValue("$id", usuarioId);
            del.ExecuteNonQuery();
        }
        foreach (var s in sucursales)
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO UsuarioSucursal (UsuarioId, Sucursal) VALUES ($id, $s)";
            ins.Parameters.AddWithValue("$id", usuarioId);
            ins.Parameters.AddWithValue("$s", s);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void QuitarSucursales(long usuarioId, IEnumerable<string> sucursales)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        foreach (var s in sucursales)
        {
            using var del = c.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM UsuarioSucursal WHERE UsuarioId = $id AND Sucursal = $s COLLATE NOCASE";
            del.Parameters.AddWithValue("$id", usuarioId);
            del.Parameters.AddWithValue("$s", s);
            del.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ------------------------------------------------------------------ auditoría
    public void Auditar(string? login, string accion, string? sucursal = null, DateTime? desde = null,
                        DateTime? hasta = null, int? filas = null, string? resultado = null, string? ip = null)
    {
        try
        {
            Ejecutar(@"INSERT INTO Auditoria (Fecha, Login, Accion, Sucursal, Desde, Hasta, Filas, Resultado, Ip)
                       VALUES ($f, $l, $a, $s, $d, $h, $n, $r, $ip)",
                ("$f", Ahora()), ("$l", login), ("$a", accion), ("$s", sucursal),
                ("$d", desde?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$h", hasta?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$n", filas), ("$r", resultado), ("$ip", ip));
        }
        catch (Exception ex) { Console.Error.WriteLine("Auditar: " + ex.Message); /* la auditoría nunca debe tumbar una consulta */ }
    }

    public List<string[]> UltimaAuditoria(int n)
    {
        var lista = new List<string[]>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT Fecha, IFNULL(Login,''), Accion, IFNULL(Sucursal,''),
                                   IFNULL(Desde,'') || CASE WHEN Hasta IS NULL THEN '' ELSE ' a ' || Hasta END,
                                   IFNULL(CAST(Filas AS TEXT),''), IFNULL(Resultado,''), IFNULL(Ip,'')
                            FROM Auditoria ORDER BY Id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var fila = new string[8];
            for (int i = 0; i < 8; i++) fila[i] = r.GetString(i);
            lista.Add(fila);
        }
        return lista;
    }
}
