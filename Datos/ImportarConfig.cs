using System.Data.Common;
using System.Xml;
using System.Xml.Linq;

namespace VentasReportesApi;

/// <summary>Una tienda leída de un archivo .config (formato &lt;connectionStrings&gt;).</summary>
public sealed record TiendaLeida(string Nombre, string Servidor, int? Puerto, string BaseDatos, string Usuario, string Clave, int Timeout);

public sealed class ResultadoImportacion
{
    public List<string> Nuevas { get; } = new();
    public List<string> Actualizadas { get; } = new();
    public List<string> SinCambios { get; } = new();
    public List<string> NoIncluidas { get; } = new();     // están en el servidor pero no en el archivo (no se tocan)
    public List<string> Errores { get; } = new();
    public List<string> Avisos { get; } = new();
}

public static class ImportadorConfig
{
    /// <summary>
    /// Lee un .config con la estructura
    ///   &lt;configuration&gt;&lt;connectionStrings&gt;&lt;add name="TIENDA" connectionString="Server=...;Database=...;User Id=...;Password=...;" /&gt;
    /// Acepta "Server=IP\INSTANCIA", "Server=NOMBREPC\INSTANCIA" y "Server=IP,PUERTO".
    /// </summary>
    public static List<TiendaLeida> Leer(string contenido, List<string> errores)
    {
        var lista = new List<TiendaLeida>();
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var sr = new StringReader(contenido.TrimStart('﻿'));
            using var xr = XmlReader.Create(sr, settings);
            doc = XDocument.Load(xr);
        }
        catch (Exception ex) { errores.Add("El archivo no es un XML válido: " + ex.Message); return lista; }

        var adds = doc.Descendants("connectionStrings").Elements("add").ToList();
        if (adds.Count == 0) errores.Add("El archivo no tiene una sección <connectionStrings> con entradas <add>.");

        foreach (var a in adds)
        {
            string nombre = ((string?)a.Attribute("name") ?? "").Trim().ToUpperInvariant();
            string cadena = (string?)a.Attribute("connectionString") ?? "";
            if (nombre.Length == 0 || cadena.Length == 0) { errores.Add("Se omitió una entrada sin nombre o sin cadena de conexión."); continue; }
            if (nombre.Equals("LOCALSQLSERVER", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var b = new DbConnectionStringBuilder { ConnectionString = cadena };
                string L(params string[] claves)
                {
                    foreach (var k in claves) if (b.TryGetValue(k, out var v) && v != null) return v.ToString()!.Trim();
                    return "";
                }
                string server = L("server", "data source", "address", "addr", "network address");
                if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) server = server[4..];
                int? puerto = null;
                int coma = server.LastIndexOf(',');
                if (coma > 0 && int.TryParse(server[(coma + 1)..].Trim(), out var p)) { puerto = p; server = server[..coma].Trim(); }
                int timeout = int.TryParse(L("connect timeout", "connection timeout", "timeout"), out var t) ? Math.Clamp(t, 5, 600) : 0;   // 0 = no indicado

                var leida = new TiendaLeida(nombre, server, puerto, L("database", "initial catalog"),
                                            L("user id", "uid", "user", "username"), L("password", "pwd"), timeout);
                if (leida.Servidor.Length == 0 || leida.BaseDatos.Length == 0 || leida.Usuario.Length == 0 || leida.Clave.Length == 0)
                { errores.Add($"{nombre}: falta servidor, base, usuario o clave; se omite."); continue; }
                if (lista.Any(x => x.Nombre == nombre)) { errores.Add($"{nombre}: aparece repetida; se usa la primera."); continue; }
                lista.Add(leida);
            }
            catch (Exception ex) { errores.Add($"{nombre}: cadena de conexión inválida ({ex.Message})."); }
        }
        return lista;
    }

    /// <summary>Compara con lo guardado y, si aplicar = true, crea/actualiza las tiendas.</summary>
    public static ResultadoImportacion Importar(string contenido, Almacen db, Cifrado cifrado, bool aplicar)
    {
        var r = new ResultadoImportacion();
        var leidas = Leer(contenido, r.Errores);
        var actuales = db.ListarTiendas();

        foreach (var x in leidas)
        {
            // Avisos útiles para un servidor Linux
            string host = x.Servidor.Split('\\')[0];
            if (!System.Net.IPAddress.TryParse(host, out _) && !host.Contains('.') &&
                !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                r.Avisos.Add($"{x.Nombre}: el servidor \"{host}\" es un nombre de equipo; un servidor Linux puede no resolverlo. Use la IP o agréguelo a /etc/hosts.");
            if (x.Usuario.Equals("sa", StringComparison.OrdinalIgnoreCase))
                r.Avisos.Add($"{x.Nombre}: usa el usuario 'sa' (administrador de SQL). Se recomienda un usuario de solo lectura.");

            var t = actuales.FirstOrDefault(a => a.Nombre.Equals(x.Nombre, StringComparison.CurrentCultureIgnoreCase));
            var nueva = new Tienda
            {
                Nombre = x.Nombre, Servidor = x.Servidor, Puerto = x.Puerto, BaseDatos = x.BaseDatos,
                Usuario = x.Usuario, TimeoutSeg = x.Timeout > 0 ? x.Timeout : (t?.TimeoutSeg ?? 30), Activa = t?.Activa ?? true
            };

            if (t == null)
            {
                r.Nuevas.Add(x.Nombre);
                if (aplicar) db.CrearTienda(nueva, cifrado.Cifrar(x.Clave));
                continue;
            }

            string claveActual;
            try { claveActual = cifrado.Descifrar(t.ClaveCifrada); } catch { claveActual = ""; }
            bool igual = t.Servidor == x.Servidor && t.Puerto == x.Puerto && t.BaseDatos == x.BaseDatos &&
                         t.Usuario == x.Usuario && claveActual == x.Clave;
            if (igual) { r.SinCambios.Add(x.Nombre); continue; }

            var cambios = new List<string>();
            if (t.Servidor != x.Servidor || t.Puerto != x.Puerto)
                cambios.Add($"servidor {t.Servidor}{(t.Puerto.HasValue ? "," + t.Puerto : "")} → {x.Servidor}{(x.Puerto.HasValue ? "," + x.Puerto : "")}");
            if (t.BaseDatos != x.BaseDatos) cambios.Add($"base {t.BaseDatos} → {x.BaseDatos}");
            if (t.Usuario != x.Usuario) cambios.Add($"usuario {t.Usuario} → {x.Usuario}");
            if (claveActual != x.Clave) cambios.Add("clave nueva");
            r.Actualizadas.Add($"{x.Nombre} ({string.Join(", ", cambios)})");
            if (aplicar)
                db.ActualizarTienda(t.Nombre, nueva, claveActual != x.Clave ? cifrado.Cifrar(x.Clave) : null);
        }

        foreach (var a in actuales)
            if (!leidas.Any(x => x.Nombre.Equals(a.Nombre, StringComparison.CurrentCultureIgnoreCase)))
                r.NoIncluidas.Add(a.Nombre);
        return r;
    }
}
