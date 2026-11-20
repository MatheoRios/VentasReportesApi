using System.Globalization;
using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>Última tasa de cambio registrada en los cierres X de la tienda (para la calculadora).</summary>
public static class ConsultaTasa
{
    public static async Task<(decimal Tasa, DateTime Fecha)?> UltimaAsync(string cadena, CancellationToken ct)
    {
        await using var conn = new SqlConnection(cadena);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Lectura.Prefijo +
            "SELECT TOP 1 TasaDeCambio, FechaCierre FROM KLK_CIERRE_X WHERE TasaDeCambio > 0 ORDER BY FechaCierre DESC", conn) { CommandTimeout = 10 };
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (Convert.ToDecimal(rd.GetValue(0), CultureInfo.InvariantCulture), Convert.ToDateTime(rd.GetValue(1), CultureInfo.InvariantCulture));
    }
}
