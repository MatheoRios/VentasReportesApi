using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>
/// "Entradas de mercancía" de una tienda (KLK_ENTRADAHDR) en un rango de fechas de creación:
/// documento, proveedor, fecha, referencia, estado (DocStatus: O = Guardado, C = Cerrado / Cancelado) y almacén.
/// Las columnas que no existan en el KLK de la tienda salen vacías.
/// </summary>
public static class ConsultaEntradas
{
    // Fila: 0 N.º documento, 1 Proveedor, 2 Fecha "yyyy-MM-dd HH:mm:ss", 3 Referencia, 4 Código de estado, 5 Estado, 6 Almacén
    private static string Sql(Dictionary<string, string> t, string colFecha)
    {
        static string S(Dictionary<string, string> t, string col, int largo = 200) =>
            t.ContainsKey(col) ? $"LTRIM(RTRIM(ISNULL(CONVERT(NVARCHAR({largo}), E.[{col}]), N'')))" : "N''";
        string fecha = t.ContainsKey("Hora") && t.TryGetValue(colFecha, out var ty) && ty == "date"
            ? $"CONVERT(CHAR(10), E.[{colFecha}], 120) + ' ' + ISNULL(CONVERT(CHAR(8), E.Hora, 108), '00:00:00')"
            : $"CONVERT(CHAR(19), E.[{colFecha}], 120)";
        string prov = t.ContainsKey("NombreProvedor") ? S(t, "NombreProvedor") : t.ContainsKey("NombreProveedor") ? S(t, "NombreProveedor") : S(t, "CodProveedor");
        return $@"
SET NOCOUNT ON;
DECLARE @desde DATETIME = @p1, @hasta DATETIME = DATEADD(DAY, 1, @p2);
SELECT {S(t, "NumDoc", 50)}, {prov}, {fecha}, {S(t, "Referencia")}, {S(t, "DocStatus", 10)},
       CASE {S(t, "DocStatus", 10)} WHEN N'O' THEN N'Guardado' WHEN N'C' THEN N'Cerrado / Cancelado' ELSE N'No definido' END,
       {S(t, "NomAlmacen")}
FROM KLK_ENTRADAHDR E
WHERE E.[{colFecha}] >= @desde AND E.[{colFecha}] < @hasta
ORDER BY E.[{colFecha}]{(t.ContainsKey("NumDoc") ? ", E.NumDoc" : "")};";
    }

    public static async Task<List<object?[]>> EjecutarAsync(string cadena, DateTime desde, DateTime hasta, int timeoutSeg, CancellationToken ct)
    {
        await using var conn = new SqlConnection(cadena);
        await conn.OpenAsync(ct);
        var t = await ConsultaCierres.ColumnasAsync(conn, cadena, "KLK_ENTRADAHDR", timeoutSeg, ct);
        if (t.Count == 0) throw new InvalidOperationException("La base de esta tienda no tiene la tabla KLK_ENTRADAHDR.");
        string? fecha = new[] { "FechaCreacion", "Fecha", "FechaDoc", "FechaEntrada" }.FirstOrDefault(t.ContainsKey);
        if (fecha == null) throw new InvalidOperationException("KLK_ENTRADAHDR no tiene la columna FechaCreacion.");
        fecha = t.Keys.First(k => string.Equals(k, fecha, StringComparison.OrdinalIgnoreCase));
        var filas = new List<object?[]>();
        await using var cmd = new SqlCommand(Lectura.Prefijo + Sql(t, fecha), conn) { CommandTimeout = timeoutSeg };
        cmd.Parameters.AddWithValue("@p1", desde.Date);
        cmd.Parameters.AddWithValue("@p2", hasta.Date);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await ConsultaCompras.Leer(rd, filas, ct);
        return filas;
    }
}
