using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>Totales de una tienda para el "Resumen de ventas por tienda".</summary>
public sealed record ResumenTienda(int Facturas, int NotasCredito, decimal VentaBruta, decimal Devoluciones,
                                   decimal VentaNeta, decimal Unidades);

/// <summary>
/// Resumen de ventas (KLK POS): facturas, notas de crédito, venta bruta, devoluciones, venta neta y unidades.
/// Misma lógica de montos que la consulta original, pero lee facturas y notas una sola vez cada una.
/// Las fechas se comparan como [desde, hasta + 1 día) para incluir todo el último día aunque la columna tenga hora.
/// </summary>
public static class ConsultaResumen
{
    private const string Sql = @"
SET NOCOUNT ON;
DECLARE @desde DATETIME = @p1, @hasta DATETIME = DATEADD(DAY, 1, @p2);
DECLARE @fac INT, @nc INT, @bruta DECIMAL(38,6), @dev DECIMAL(38,6), @uVen DECIMAL(38,6), @uDev DECIMAL(38,6);

SELECT @fac = COUNT(NumTicketFiscal)
FROM KLK_FACTURAHDR
WHERE FechaFactura >= @desde AND FechaFactura < @hasta AND EnEspera = 0;

SELECT @nc = COUNT(NumNota)
FROM KLK_NCREDITOHDR
WHERE FechaNota >= @desde AND FechaNota < @hasta;

SELECT @bruta = ISNULL(SUM(FL.TotalLineaUsd + FL.IVAUSD - (ISNULL(FL.DescuentoUSD, 0) + ISNULL(FL.MontoDescuentoFidelUsd, 0) + ISNULL(FL.MontoDescuentoFormPagoUsd, 0))), 0),
       @uVen  = ISNULL(SUM(ISNULL(FL.Cantidad, 0)), 0)
FROM KLK_FACTURAHDR AS FH
INNER JOIN KLK_FACTURALINE FL ON FH.NumFactura = FL.NumFactura AND FH.CodCaja = FL.CodCaja AND FH.CodApertura = FL.CodApertura
WHERE FH.FechaFactura >= @desde AND FH.FechaFactura < @hasta AND FH.EnEspera = 0;

SELECT @dev  = ISNULL(SUM(NL.TotalLineaUsd + NL.IVAUSD - (ISNULL(NL.DescuentoUSD, 0) + ISNULL(NL.MontoDescuentoFidelUsd, 0) + ISNULL(NL.MontoDescuentoFormPagoUsd, 0))), 0),
       @uDev = ISNULL(SUM(ISNULL(NL.Cantidad, 0)), 0)
FROM KLK_NCREDITOHDR AS NH
INNER JOIN KLK_NCREDITOLINE NL ON NH.NumNota = NL.NumNota AND NH.CodCaja = NL.CodCaja AND NH.CodApertura = NL.Orden
WHERE NH.FechaNota >= @desde AND NH.FechaNota < @hasta;

SELECT ISNULL(@fac, 0)                          AS Facturas,
       ISNULL(@nc, 0)                           AS NotasCredito,
       CAST(ISNULL(@bruta, 0) AS DECIMAL(18,2))  AS VentaBruta,
       CAST(ISNULL(@dev, 0) AS DECIMAL(18,2))    AS Devoluciones,
       CAST(ISNULL(@bruta, 0) - ISNULL(@dev, 0) AS DECIMAL(18,2)) AS VentaNeta,
       CAST(ISNULL(@uVen, 0) - ISNULL(@uDev, 0) AS DECIMAL(18,2)) AS Unidades;";

    public static async Task<ResumenTienda> EjecutarAsync(string cadena, DateTime desde, DateTime hasta,
                                                          int timeoutSeg, CancellationToken ct)
    {
        await using var conn = new SqlConnection(cadena);
        await using var cmd = new SqlCommand(Lectura.Prefijo + Sql, conn) { CommandTimeout = timeoutSeg };
        cmd.Parameters.AddWithValue("@p1", desde.Date);
        cmd.Parameters.AddWithValue("@p2", hasta.Date);
        await conn.OpenAsync(ct);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return new ResumenTienda(0, 0, 0, 0, 0, 0);
        return new ResumenTienda(Convert.ToInt32(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1)),
                                 Convert.ToDecimal(rd.GetValue(2)), Convert.ToDecimal(rd.GetValue(3)),
                                 Convert.ToDecimal(rd.GetValue(4)), Convert.ToDecimal(rd.GetValue(5)));
    }
}
