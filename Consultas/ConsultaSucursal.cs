using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

public sealed record ColumnaInfo(string Nombre, string Tipo);

public sealed class ResultadoReporte
{
    public List<ColumnaInfo> Columnas { get; } = new();
    public List<object?[]> Filas { get; } = new();
}

/// <summary>Ejecuta dbo.getVentasPosStore en la base de una tienda.</summary>
public static class ConsultaSucursal
{
    public static async Task<ResultadoReporte> EjecutarAsync(string cadena, DateTime desde, DateTime hasta,
                                                             int timeoutSeg, CancellationToken ct)
    {
        var res = new ResultadoReporte();
        await using var conn = new SqlConnection(cadena);
        await using var cmd = new SqlCommand(Lectura.Prefijo + "EXEC dbo.getVentasPosStore @FechaInicio, @FechaFin", conn)
        {
            CommandTimeout = timeoutSeg
        };
        cmd.Parameters.AddWithValue("@FechaInicio", desde.Date);
        cmd.Parameters.AddWithValue("@FechaFin", hasta.Date);

        await conn.OpenAsync(ct);
        await using var rd = await cmd.ExecuteReaderAsync(ct);

        int n = rd.FieldCount;
        for (int i = 0; i < n; i++)
            res.Columnas.Add(new ColumnaInfo(rd.GetName(i), TipoDe(rd.GetFieldType(i))));

        while (await rd.ReadAsync(ct))
        {
            var fila = new object?[n];
            for (int i = 0; i < n; i++)
                fila[i] = rd.IsDBNull(i) ? null : Normalizar(rd.GetValue(i));
            res.Filas.Add(fila);
        }
        return res;
    }

    // Tipos simples que el complemento sabe convertir para Excel
    private static string TipoDe(Type t)
    {
        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float)) return "decimal";
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)) return "entero";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "fecha";
        if (t == typeof(TimeSpan)) return "hora";
        if (t == typeof(bool)) return "logico";
        return "texto";
    }

    private static object? Normalizar(object v) => v switch
    {
        DateTimeOffset dto => dto.DateTime,
        TimeSpan ts => ts.ToString("c"),
        byte[] => "(binario)",
        Guid g => g.ToString(),
        double d when double.IsNaN(d) || double.IsInfinity(d) => null,
        float f when float.IsNaN(f) || float.IsInfinity(f) => null,
        _ => v
    };

    public static string MensajeAmigable(Exception ex) => ex switch
    {
        SqlException s when s.Number is -2 or 53 or 2 or 258 or 10060 or 11001 => "Sin conexión con el servidor de la tienda",
        SqlException s when s.Number == 18456 => "Usuario o contraseña de SQL inválidos",
        SqlException s when s.Number == 2812 => "No existe dbo.getVentasPosStore en esa base",
        SqlException s when s.Number == 4060 => "No se pudo abrir la base de datos de la tienda",
        _ => ex.Message.Length > 150 ? ex.Message[..150] + "…" : ex.Message
    };
}
