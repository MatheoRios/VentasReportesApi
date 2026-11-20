using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace VentasReportesApi;

// Chat: tablas y consultas en datos/seguridad.db (ChatMensajes, ChatLeidos, ChatPrivados, archivos).
public sealed partial class Almacen
{
    public void InicializarChat(int diasHistorial)
    {
        using var c = Abrir();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ChatMensajes (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Canal     TEXT NOT NULL,
    Login     TEXT NOT NULL,
    Nombre    TEXT NOT NULL,
    Texto     TEXT NOT NULL,
    Adjunto   TEXT NULL,
    Fecha     TEXT NOT NULL,
    Eliminado INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_Chat_Canal ON ChatMensajes(Canal, Id);
CREATE INDEX IF NOT EXISTS IX_Chat_Fecha ON ChatMensajes(Fecha);
CREATE TABLE IF NOT EXISTS ChatLeidos (
    Login    TEXT NOT NULL COLLATE NOCASE,
    Canal    TEXT NOT NULL,
    UltimoId INTEGER NOT NULL,
    PRIMARY KEY (Login, Canal)
);
CREATE TABLE IF NOT EXISTS ChatPrivados (
    Canal TEXT PRIMARY KEY,
    A     TEXT NOT NULL COLLATE NOCASE,
    B     TEXT NOT NULL COLLATE NOCASE
);
CREATE INDEX IF NOT EXISTS IX_ChatPriv_A ON ChatPrivados(A);
CREATE INDEX IF NOT EXISTS IX_ChatPriv_B ON ChatPrivados(B);
CREATE TABLE IF NOT EXISTS ChatArchivos (
    Id        TEXT PRIMARY KEY,
    Canal     TEXT NOT NULL,
    Login     TEXT NOT NULL COLLATE NOCASE,
    Nombre    TEXT NOT NULL,
    Tipo      TEXT NOT NULL,
    Tamano    INTEGER NOT NULL,
    Fecha     TEXT NOT NULL,
    MensajeId INTEGER NULL
);
CREATE INDEX IF NOT EXISTS IX_ChatArchivos_MensajeId ON ChatArchivos(MensajeId);";
            cmd.ExecuteNonQuery();
        }
        PurgarChat(diasHistorial);
    }

    /// <summary>Borra los mensajes más viejos que 'dias' (se llama al iniciar y cada noche).</summary>
    public int PurgarChat(int dias)
    {
        if (dias <= 0) return 0;
        return Ejecutar("DELETE FROM ChatMensajes WHERE Fecha < $f",
                        ("$f", DateTime.Now.AddDays(-dias).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    private const string ColumnasChat = "Id, Canal, Login, Nombre, Texto, IFNULL(Adjunto,''), Fecha, Eliminado";

    private static ChatMensaje LeerMensaje(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        string adj = r.GetString(5);
        return new ChatMensaje(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                               r.GetString(6), adj.Length == 0 ? null : adj, r.GetInt64(7) == 1);
    }

    /// <summary>Guarda el mensaje, lo marca leído para el autor y lo devuelve (una sola conexión).</summary>
    public ChatMensaje InsertarMensaje(string canal, string login, string nombre, string texto, string? adjunto)
    {
        using var c = Abrir();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO ChatMensajes (Canal, Login, Nombre, Texto, Adjunto, Fecha)
                            VALUES ($c, $l, $n, $t, $a, $f);
                            INSERT INTO ChatLeidos (Login, Canal, UltimoId) VALUES ($l, $c, last_insert_rowid())
                              ON CONFLICT(Login, Canal) DO UPDATE SET UltimoId = MAX(UltimoId, excluded.UltimoId);
                            SELECT " + ColumnasChat + " FROM ChatMensajes WHERE Id = (SELECT UltimoId FROM ChatLeidos WHERE Login = $l AND Canal = $c);";
        string fecha = Ahora();
        cmd.Parameters.AddWithValue("$c", canal);
        cmd.Parameters.AddWithValue("$l", login);
        cmd.Parameters.AddWithValue("$n", nombre);
        cmd.Parameters.AddWithValue("$t", texto);
        cmd.Parameters.AddWithValue("$a", (object?)adjunto ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", fecha);
        ChatMensaje m;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException("No se pudo guardar el mensaje.");
            m = LeerMensaje(r);
        }
        tx.Commit();
        return m;
    }

    /// <summary>Mensajes eliminados del canal desde un Id (para quitarlos de la pantalla).</summary>
    public List<long> EliminadosDesde(string canal, long desdeId)
    {
        var lista = new List<long>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id FROM ChatMensajes WHERE Canal = $c AND Id >= $d AND Eliminado = 1";
        cmd.Parameters.AddWithValue("$c", canal);
        cmd.Parameters.AddWithValue("$d", desdeId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add(r.GetInt64(0));
        return lista;
    }

    public ChatMensaje? ObtenerMensaje(long id)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ColumnasChat} FROM ChatMensajes WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? LeerMensaje(r) : null;
    }

    /// <summary>despues &gt; 0: mensajes nuevos. antes &gt; 0: historial anterior. Ninguno: los últimos.</summary>
    public List<ChatMensaje> Mensajes(string canal, long despues, long antes, int limite)
    {
        var lista = new List<ChatMensaje>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        if (despues > 0)
            cmd.CommandText = $"SELECT {ColumnasChat} FROM ChatMensajes WHERE Canal = $c AND Id > $x ORDER BY Id LIMIT $lim";
        else if (antes > 0)
            cmd.CommandText = $"SELECT {ColumnasChat} FROM ChatMensajes WHERE Canal = $c AND Id < $x ORDER BY Id DESC LIMIT $lim";
        else
            cmd.CommandText = $"SELECT {ColumnasChat} FROM ChatMensajes WHERE Canal = $c ORDER BY Id DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$c", canal);
        cmd.Parameters.AddWithValue("$x", despues > 0 ? despues : antes);
        cmd.Parameters.AddWithValue("$lim", limite);
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add(LeerMensaje(r));
        if (despues <= 0) lista.Reverse();
        return lista;
    }

    /// <summary>Mensajes alrededor de uno (para abrir un resultado de la búsqueda).</summary>
    public (List<ChatMensaje> lista, bool hayMas, bool hayNuevos) MensajesAlrededor(string canal, long id, int mitad)
    {
        var antes = new List<ChatMensaje>();
        var despues = new List<ChatMensaje>();
        using var c = Abrir();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ColumnasChat} FROM ChatMensajes WHERE Canal = $c AND Id <= $x ORDER BY Id DESC LIMIT $lim";
            cmd.Parameters.AddWithValue("$c", canal);
            cmd.Parameters.AddWithValue("$x", id);
            cmd.Parameters.AddWithValue("$lim", mitad);
            using var r = cmd.ExecuteReader();
            while (r.Read()) antes.Add(LeerMensaje(r));
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ColumnasChat} FROM ChatMensajes WHERE Canal = $c AND Id > $x ORDER BY Id LIMIT $lim";
            cmd.Parameters.AddWithValue("$c", canal);
            cmd.Parameters.AddWithValue("$x", id);
            cmd.Parameters.AddWithValue("$lim", mitad + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read()) despues.Add(LeerMensaje(r));
        }
        bool hayNuevos = despues.Count > mitad;
        if (hayNuevos) despues.RemoveAt(despues.Count - 1);
        antes.Reverse();
        antes.AddRange(despues);
        return (antes, antes.Count > 0 && antes.Count - despues.Count == mitad, hayNuevos);
    }

    // Sin tildes y en minúsculas: "Café" encuentra "cafe" y al revés
    internal static string SinTildes(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Busca texto (o nombre de archivo) en los mensajes de los canales indicados, del más nuevo al más viejo.</summary>
    public List<ChatMensaje> BuscarMensajes(IList<string> canales, string texto, int limite)
    {
        var lista = new List<ChatMensaje>();
        if (canales.Count == 0) return lista;
        string q = SinTildes(texto).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        string enLista = string.Join(",", canales.Select((_, i) => "$c" + i));
        using var c = Abrir();
        c.CreateFunction("sintildes", (string? x) => SinTildes(x), isDeterministic: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $@"SELECT {ColumnasChat} FROM ChatMensajes
                             WHERE Canal IN ({enLista}) AND Eliminado = 0
                               AND (sintildes(Texto) LIKE $q ESCAPE '\'
                                    OR sintildes(IFNULL(json_extract(Adjunto, '$.ArchivoNombre'), '')) LIKE $q ESCAPE '\')
                             ORDER BY Id DESC LIMIT $lim";
        for (int i = 0; i < canales.Count; i++) cmd.Parameters.AddWithValue("$c" + i, canales[i]);
        cmd.Parameters.AddWithValue("$q", "%" + q + "%");
        cmd.Parameters.AddWithValue("$lim", limite);
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add(LeerMensaje(r));
        return lista;
    }

    // ---- archivos adjuntos (el contenido va en datos/adjuntos, aquí solo los datos) ----
    public void RegistrarArchivo(ChatArchivo a) =>
        Ejecutar(@"INSERT INTO ChatArchivos (Id, Canal, Login, Nombre, Tipo, Tamano, Fecha) VALUES ($i, $c, $l, $n, $t, $s, $f)",
                 ("$i", a.Id), ("$c", a.Canal), ("$l", a.Login), ("$n", a.Nombre), ("$t", a.Tipo), ("$s", a.Tamano), ("$f", a.Fecha));

    public ChatArchivo? ObtenerArchivo(string id)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Canal, Login, Nombre, Tipo, Tamano, Fecha, MensajeId FROM ChatArchivos WHERE Id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ChatArchivo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5),
                               r.GetString(6), r.IsDBNull(7) ? null : r.GetInt64(7));
    }

    public bool VincularArchivo(string id, long mensajeId) =>
        Ejecutar("UPDATE ChatArchivos SET MensajeId = $m WHERE Id = $i AND MensajeId IS NULL", ("$m", mensajeId), ("$i", id)) > 0;

    public void BorrarArchivo(string id) => Ejecutar("DELETE FROM ChatArchivos WHERE Id = $i", ("$i", id));

    /// <summary>Archivos que ya no sirven: subidos y nunca enviados (1 día), o de mensajes eliminados o purgados.</summary>
    public List<string> ArchivosSobrantes()
    {
        var lista = new List<string>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT a.Id FROM ChatArchivos a
                            WHERE (a.MensajeId IS NULL AND a.Fecha < $ayer)
                               OR (a.MensajeId IS NOT NULL AND NOT EXISTS
                                   (SELECT 1 FROM ChatMensajes m WHERE m.Id = a.MensajeId AND m.Eliminado = 0))";
        cmd.Parameters.AddWithValue("$ayer", DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add(r.GetString(0));
        return lista;
    }

    public bool EliminarMensaje(long id) =>
        Ejecutar("UPDATE ChatMensajes SET Eliminado = 1, Texto = '', Adjunto = NULL WHERE Id = $id", ("$id", id)) > 0;

    /// <summary>Marca leído hasta ese mensaje. Devuelve false si ya estaba leído (no hubo cambio).</summary>
    public bool MarcarLeido(string login, string canal, long hastaId) =>
        Ejecutar(@"INSERT INTO ChatLeidos (Login, Canal, UltimoId) VALUES ($l, $c, $i)
                   ON CONFLICT(Login, Canal) DO UPDATE SET UltimoId = excluded.UltimoId WHERE excluded.UltimoId > ChatLeidos.UltimoId",
                 ("$l", login), ("$c", canal), ("$i", hastaId)) > 0;

    public void RegistrarPrivado(string canal, string a, string b) =>
        Ejecutar("INSERT OR IGNORE INTO ChatPrivados (Canal, A, B) VALUES ($c, $a, $b)", ("$c", canal), ("$a", a), ("$b", b));

    /// <summary>Logins con los que el usuario tiene conversación privada (canal, el otro).</summary>
    public List<(string canal, string otro)> PrivadosDe(string login)
    {
        var lista = new List<(string, string)>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Canal, CASE WHEN A = $l THEN B ELSE A END FROM ChatPrivados WHERE A = $l OR B = $l";
        cmd.Parameters.AddWithValue("$l", login);
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add((r.GetString(0), r.GetString(1)));
        return lista;
    }

    /// <summary>Por canal: mensajes sin leer (de otros) y el último mensaje.</summary>
    public Dictionary<string, (int noLeidos, ChatMensaje? ultimo)> ResumenCanales(string login, IList<string> canales)
    {
        var res = new Dictionary<string, (int, ChatMensaje?)>();
        if (canales.Count == 0) return res;
        string lista = string.Join(",", canales.Select((_, i) => "$c" + i));

        using var c = Abrir();
        var noLeidos = new Dictionary<string, int>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $@"SELECT m.Canal, COUNT(*) FROM ChatMensajes m
                                 LEFT JOIN ChatLeidos l ON l.Canal = m.Canal AND l.Login = $me
                                 WHERE m.Canal IN ({lista}) AND m.Id > IFNULL(l.UltimoId, 0)
                                   AND m.Login <> $me COLLATE NOCASE AND m.Eliminado = 0
                                 GROUP BY m.Canal";
            cmd.Parameters.AddWithValue("$me", login);
            for (int i = 0; i < canales.Count; i++) cmd.Parameters.AddWithValue("$c" + i, canales[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) noLeidos[r.GetString(0)] = (int)r.GetInt64(1);
        }
        var ultimos = new Dictionary<string, ChatMensaje>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ColumnasChat} FROM ChatMensajes WHERE Id IN (SELECT MAX(Id) FROM ChatMensajes WHERE Canal IN ({lista}) GROUP BY Canal)";
            for (int i = 0; i < canales.Count; i++) cmd.Parameters.AddWithValue("$c" + i, canales[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var m = LeerMensaje(r); ultimos[m.Canal] = m; }
        }
        foreach (var canal in canales)
            res[canal] = (noLeidos.TryGetValue(canal, out var n) ? n : 0, ultimos.TryGetValue(canal, out var u) ? u : null);
        return res;
    }
}
