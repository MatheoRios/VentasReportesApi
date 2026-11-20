using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>
/// "Ventas diarias": una fila por día con facturas, notas de crédito, venta bruta, devoluciones y venta neta.
/// Agrupa por día (CAST AS DATE) aunque la fecha guarde la hora, usa el índice de fecha y cuenta las facturas
/// igual que el Resumen por tienda (COUNT(NumTicketFiscal)) para que los totales cuadren entre reportes.
/// </summary>
public static class ConsultaDiaria
{
    private const string Sql = @"
SET NOCOUNT ON;
DECLARE @ini DATETIME = @p1, @fin DATETIME = DATEADD(DAY, 1, @p2);
WITH Fac AS (
    SELECT CAST(FechaFactura AS DATE) AS Dia, COUNT(NumTicketFiscal) AS Facturas
    FROM KLK_FACTURAHDR
    WHERE FechaFactura >= @ini AND FechaFactura < @fin AND EnEspera = 0
    GROUP BY CAST(FechaFactura AS DATE)
),
FacMonto AS (
    SELECT CAST(FH.FechaFactura AS DATE) AS Dia,
           SUM(FL.TotalLineaUsd + FL.IVAUSD - (ISNULL(FL.DescuentoUSD,0) + ISNULL(FL.MontoDescuentoFidelUsd,0) + ISNULL(FL.MontoDescuentoFormPagoUsd,0))) AS Bruta
    FROM KLK_FACTURAHDR FH
    INNER JOIN KLK_FACTURALINE FL ON FH.NumFactura = FL.NumFactura AND FH.CodCaja = FL.CodCaja AND FH.CodApertura = FL.CodApertura
    WHERE FH.FechaFactura >= @ini AND FH.FechaFactura < @fin AND FH.EnEspera = 0
    GROUP BY CAST(FH.FechaFactura AS DATE)
),
Nc AS (
    SELECT CAST(FechaNota AS DATE) AS Dia, COUNT(NumNota) AS Notas
    FROM KLK_NCREDITOHDR
    WHERE FechaNota >= @ini AND FechaNota < @fin
    GROUP BY CAST(FechaNota AS DATE)
),
NcMonto AS (
    SELECT CAST(NH.FechaNota AS DATE) AS Dia,
           SUM(NL.TotalLineaUsd + NL.IVAUSD - (ISNULL(NL.DescuentoUSD,0) + ISNULL(NL.MontoDescuentoFidelUsd,0) + ISNULL(NL.MontoDescuentoFormPagoUsd,0))) AS Dev
    FROM KLK_NCREDITOHDR NH
    INNER JOIN KLK_NCREDITOLINE NL ON NH.NumNota = NL.NumNota AND NH.CodCaja = NL.CodCaja AND NH.CodApertura = NL.Orden
    WHERE NH.FechaNota >= @ini AND NH.FechaNota < @fin
    GROUP BY CAST(NH.FechaNota AS DATE)
),
Dias AS (
    SELECT Dia FROM Fac UNION SELECT Dia FROM FacMonto UNION SELECT Dia FROM Nc UNION SELECT Dia FROM NcMonto
)
SELECT CONVERT(CHAR(10), D.Dia, 120)                                        AS Fecha,
       ISNULL(F.Facturas, 0)                                                 AS Facturas,
       ISNULL(N.Notas, 0)                                                    AS NotasCredito,
       CAST(ISNULL(FM.Bruta, 0) AS DECIMAL(18,2))                            AS VentaBruta,
       CAST(ISNULL(NM.Dev, 0) AS DECIMAL(18,2))                              AS Devoluciones,
       CAST(ISNULL(FM.Bruta, 0) - ISNULL(NM.Dev, 0) AS DECIMAL(18,2))        AS VentaNeta
FROM Dias D
LEFT JOIN Fac F       ON F.Dia = D.Dia
LEFT JOIN FacMonto FM ON FM.Dia = D.Dia
LEFT JOIN Nc N        ON N.Dia = D.Dia
LEFT JOIN NcMonto NM  ON NM.Dia = D.Dia
ORDER BY D.Dia;";

    public static async Task<List<object?[]>> EjecutarAsync(string cadena, DateTime desde, DateTime hasta, int timeoutSeg, CancellationToken ct)
    {
        var filas = new List<object?[]>();
        await using var conn = new SqlConnection(cadena);
        await using var cmd = new SqlCommand(Lectura.Prefijo + Sql, conn) { CommandTimeout = timeoutSeg };
        cmd.Parameters.AddWithValue("@p1", desde.Date);
        cmd.Parameters.AddWithValue("@p2", hasta.Date);
        await conn.OpenAsync(ct);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            filas.Add(new object?[]
            {
                Convert.ToString(rd.GetValue(0))!.Trim(), Convert.ToInt32(rd.GetValue(1)), Convert.ToInt32(rd.GetValue(2)),
                Convert.ToDecimal(rd.GetValue(3)), Convert.ToDecimal(rd.GetValue(4)), Convert.ToDecimal(rd.GetValue(5))
            });
        return filas;
    }
}
