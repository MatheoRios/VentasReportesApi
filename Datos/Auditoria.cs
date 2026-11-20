using System.Globalization;
using System.Text;

namespace VentasReportesApi;

public sealed record AuditoriaFila(long Id, string Fecha, string Login, string Accion, string Tipo, string Sucursal,
                                   string Desde, string Hasta, int? Filas, string Resultado, string Ip, bool Problema);

public sealed record FiltroAuditoria(DateTime Desde, DateTime Hasta, string? Usuario, string? Tienda, string? Tipo,
                                     string? Texto, int Limite, bool IncluirTiendas);

public sealed partial class Almacen
{
    // Patrones de los registros antiguos (acción "ADMIN") que eran cambios de tiendas
    private const string EsCambioTiendaViejo =
        "(Accion = 'ADMIN' AND (Resultado LIKE 'Creó tienda%' OR Resultado LIKE 'Editó tienda%' OR Resultado LIKE 'Eliminó tienda%'))";

    /// <summary>Migraciones simples de esquema (columnas nuevas).</summary>
    public void Migrar()
    {
        using var c = Abrir();
        bool tiene = false;
        using (var info = c.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Usuarios)";
            using var r = info.ExecuteReader();
            while (r.Read()) if (r.GetString(1).Equals("DebeCambiarClave", StringComparison.OrdinalIgnoreCase)) tiene = true;
        }
        if (!tiene)
        {
            using var alt = c.CreateCommand();
            alt.CommandText = "ALTER TABLE Usuarios ADD COLUMN DebeCambiarClave INTEGER NOT NULL DEFAULT 0";
            alt.ExecuteNonQuery();
        }
        bool tienePermisos = false;
        using (var info = c.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Usuarios)";
            using var r = info.ExecuteReader();
            while (r.Read()) if (r.GetString(1).Equals("Permisos", StringComparison.OrdinalIgnoreCase)) tienePermisos = true;
        }
        if (!tienePermisos)
        {
            using var alt = c.CreateCommand();
            alt.CommandText = "ALTER TABLE Usuarios ADD COLUMN Permisos TEXT NULL";   // NULL = todos (usuarios anteriores)
            alt.ExecuteNonQuery();
        }
        bool tieneTokenVersion = false;
        using (var info = c.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Usuarios)";
            using var r = info.ExecuteReader();
            while (r.Read()) if (r.GetString(1).Equals("TokenVersion", StringComparison.OrdinalIgnoreCase)) tieneTokenVersion = true;
        }
        if (!tieneTokenVersion)
        {
            using var alt = c.CreateCommand();
            alt.CommandText = "ALTER TABLE Usuarios ADD COLUMN TokenVersion INTEGER NOT NULL DEFAULT 1";
            alt.ExecuteNonQuery();
        }
        using (var mig = c.CreateCommand())
        {
            mig.CommandText = "CREATE TABLE IF NOT EXISTS Migraciones (Nombre TEXT PRIMARY KEY, Fecha TEXT NOT NULL);";
            mig.ExecuteNonQuery();
        }
        // Una sola vez por reporte nuevo: quien ya tenía el reporte de ventas lo recibe también
        // (resumen por tienda: 1.8.0 · ventas por categoría: 1.9.0 · ventas diarias: 1.10.0 · resumen por artículo: 1.12.0)
        foreach (var nuevo in new[] { "resumen", "categorias", "diario", "articulos" })
        {
            using var tx = c.BeginTransaction();
            using var m = c.CreateCommand();
            m.Transaction = tx;
            m.CommandText = @"INSERT OR IGNORE INTO Migraciones (Nombre, Fecha) VALUES ('permiso_' || $p, datetime('now','localtime'));
                              SELECT changes();";
            m.Parameters.AddWithValue("$p", nuevo);
            if (Convert.ToInt64(m.ExecuteScalar()) == 1)
            {
                using var u = c.CreateCommand();
                u.Transaction = tx;
                u.CommandText = @"UPDATE Usuarios SET Permisos = Permisos || ',' || $p
                                  WHERE Permisos IS NOT NULL AND (',' || Permisos || ',') LIKE '%,ventas,%'
                                    AND (',' || Permisos || ',') NOT LIKE '%,' || $p || ',%'";
                u.Parameters.AddWithValue("$p", nuevo);
                u.ExecuteNonQuery();
            }
            tx.Commit();
        }
        // Análisis de productos (1.19.1): permiso propio; quien ya tenía "Resumen por artículo" (con el que se usaba) lo recibe
        {
            using var tx = c.BeginTransaction();
            using var m = c.CreateCommand();
            m.Transaction = tx;
            m.CommandText = @"INSERT OR IGNORE INTO Migraciones (Nombre, Fecha) VALUES ('permiso_productos', datetime('now','localtime'));
                              SELECT changes();";
            if (Convert.ToInt64(m.ExecuteScalar()) == 1)
            {
                using var u = c.CreateCommand();
                u.Transaction = tx;
                u.CommandText = @"UPDATE Usuarios SET Permisos = Permisos || ',productos'
                                  WHERE Permisos IS NOT NULL AND (',' || Permisos || ',') LIKE '%,articulos,%'
                                    AND (',' || Permisos || ',') NOT LIKE '%,productos,%'";
                u.ExecuteNonQuery();
            }
            tx.Commit();
        }
        using var idx = c.CreateCommand();
        idx.CommandText = "CREATE INDEX IF NOT EXISTS IX_Auditoria_Login ON Auditoria(Login); CREATE INDEX IF NOT EXISTS IX_Auditoria_Accion ON Auditoria(Accion);";
        idx.ExecuteNonQuery();
    }

    public bool MarcarCambioClave(string login, bool debe) =>
        Ejecutar("UPDATE Usuarios SET DebeCambiarClave = $d WHERE Login = $l", ("$d", debe ? 1 : 0), ("$l", login)) > 0;

    public bool DebeCambiarClave(long id)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DebeCambiarClave FROM Usuarios WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 1;
    }

    public (List<AuditoriaFila> filas, int total) BuscarAuditoria(FiltroAuditoria f)
    {
        var where = new StringBuilder("Fecha >= $desde AND Fecha < $hasta");
        var p = new List<(string, object?)>
        {
            ("$desde", f.Desde.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$hasta", f.Hasta.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(f.Usuario)) { where.Append(" AND Login = $u COLLATE NOCASE"); p.Add(("$u", f.Usuario.Trim())); }
        if (!string.IsNullOrWhiteSpace(f.Tienda)) { where.Append(" AND Sucursal = $t COLLATE NOCASE"); p.Add(("$t", f.Tienda.Trim())); }
        if (!string.IsNullOrWhiteSpace(f.Texto))
        {
            where.Append(" AND (Resultado LIKE $x OR Login LIKE $x OR Sucursal LIKE $x OR Ip LIKE $x)");
            p.Add(("$x", "%" + f.Texto.Trim() + "%"));
        }
        switch ((f.Tipo ?? "").ToLowerInvariant())
        {
            case "reportes": where.Append(" AND Accion = 'REPORTE'"); break;
            case "login": where.Append(" AND Accion = 'LOGIN'"); break;
            case "usuarios": where.Append($" AND (Accion IN ('USUARIO','CLAVE') OR (Accion = 'ADMIN' AND NOT {EsCambioTiendaViejo}))"); break;
            case "tiendas": where.Append($" AND (Accion = 'TIENDA' OR {EsCambioTiendaViejo})"); break;
            case "denegados": where.Append(" AND (Accion = 'DENEGADO' OR (Accion = 'LOGIN' AND IFNULL(Resultado,'') <> 'OK'))"); break;
            case "errores": where.Append(" AND Accion = 'REPORTE' AND Resultado LIKE 'Error%'"); break;
        }
        if (!f.IncluirTiendas) where.Append($" AND NOT (Accion = 'TIENDA' OR {EsCambioTiendaViejo})");

        using var c = Abrir();
        int total;
        using (var cnt = c.CreateCommand())
        {
            cnt.CommandText = "SELECT COUNT(*) FROM Auditoria WHERE " + where;
            foreach (var (n, v) in p) cnt.Parameters.AddWithValue(n, v ?? DBNull.Value);
            total = Convert.ToInt32(cnt.ExecuteScalar());
        }

        var filas = new List<AuditoriaFila>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT Id, Fecha, IFNULL(Login,''), Accion, IFNULL(Sucursal,''), IFNULL(Desde,''), IFNULL(Hasta,''),
                                   Filas, IFNULL(Resultado,''), IFNULL(Ip,'')
                            FROM Auditoria WHERE " + where + " ORDER BY Id DESC LIMIT $lim";
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lim", Math.Clamp(f.Limite, 1, 20000));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string accion = r.GetString(3), resultado = r.GetString(8);
            string tipo = accion switch
            {
                "REPORTE" => resultado.StartsWith("Error") ? "Error en reporte" : resultado.StartsWith("Cancelado") ? "Reporte cancelado" : "Reporte",
                "LOGIN" => resultado == "OK" ? "Inicio de sesión" : "Inicio fallido",
                "DENEGADO" => "Acceso denegado",
                "USUARIO" => "Cambio de usuario",
                "TIENDA" => "Cambio de tienda",
                "CLAVE" => "Cambio de clave",
                "ADMIN" => resultado.Contains(" tienda ") || resultado.StartsWith("Creó tienda") || resultado.StartsWith("Editó tienda") || resultado.StartsWith("Eliminó tienda")
                           ? "Cambio de tienda" : "Cambio de usuario",
                _ => accion
            };
            bool problema = tipo is "Error en reporte" or "Inicio fallido" or "Acceso denegado";
            filas.Add(new AuditoriaFila(r.GetInt64(0), r.GetString(1), r.GetString(2), accion, tipo, r.GetString(4),
                                        r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : (int)r.GetInt64(7),
                                        resultado, r.GetString(9), problema));
        }
        return (filas, total);
    }
}
