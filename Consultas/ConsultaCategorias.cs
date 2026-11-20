using System.Globalization;
using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>
/// "Ventas por categoría": una fila por artículo (ventas − devoluciones) con categoría, existencia,
/// cantidades, base, IVA y costo en USD y Bs. Corrige la consulta original:
///  - parte de las líneas vendidas (LEFT JOIN al maestro): nada se pierde si el artículo no está en KLK_SAP_ARTICULO;
///  - fechas por rango [desde, hasta+1) que usan el índice (sin TRY_CONVERT sobre la columna);
///  - existencia sumada aparte en los almacenes de venta (no esconde negativos);
///  - filtros opcionales con NULL y OPTION (RECOMPILE) para que cada combinación tenga su mejor plan.
/// Supuesto: CostoUsd/CostoBs de la línea son el costo TOTAL de la línea (igual que la consulta original).
/// </summary>
public static class ConsultaCategorias
{
    internal static string ConstruirSql(Dictionary<string, string> nh, Dictionary<string, string> nl)
    {
        string ordenIgual = nl.ContainsKey("Orden") && nh.ContainsKey("CodApertura")
            ? ConsultaCierres.Igual("N.Orden", ConsultaCierres.Tipo(nl, "Orden"), "NH.CodApertura", ConsultaCierres.Tipo(nh, "CodApertura"))
            : "N.Orden = NH.CodApertura";
        string filtroNoFiscal = nh.ContainsKey("NoFiscal") ? "\n      AND (@noFiscal IS NULL OR NH.NoFiscal = @noFiscal)" : "";
        string filtroCredito = nh.ContainsKey("FacturaACredito") ? "\n      AND (@credito IS NULL OR NH.FacturaACredito = @credito)" : "";
        return $@"
SET NOCOUNT ON;
DECLARE @ini DATETIME = @p1, @fin DATETIME = DATEADD(DAY, 1, @p2);
DECLARE @caja NVARCHAR(30) = NULLIF(LTRIM(RTRIM(@pCaja)), N''),
        @noFiscal BIT = TRY_CONVERT(BIT, NULLIF(@pNoFiscal, N'')),
        @credito  BIT = TRY_CONVERT(BIT, NULLIF(@pCredito, N''));

WITH Lineas AS (
    SELECT F.CodArticulo, F.CodigoAlmacen, F.Cantidad,
           F.TotalLineaUsd - (ISNULL(F.DescuentoUSD,0) + ISNULL(F.MontoDescuentoFidelUsd,0) + ISNULL(F.MontoDescuentoFormPagoUsd,0)) AS BaseUsd,
           ISNULL(F.IvaUSD,0) AS IvaUsd, ISNULL(F.CostoUsd,0) AS CostoUsd,
           F.TotalLinea - (ISNULL(F.MontoDescuento,0) + ISNULL(F.MontoDescuentoFidel,0) + ISNULL(F.MontoDescuentoFormPago,0)) AS BaseBs,
           ISNULL(F.MontoImpuesto,0) AS IvaBs, ISNULL(F.CostoBs,0) AS CostoBs
    FROM KLK_FACTURAHDR H
    INNER JOIN KLK_FACTURALINE F ON F.NumFactura = H.NumFactura AND F.CodCaja = H.CodCaja AND F.CodApertura = H.CodApertura
    WHERE H.FechaFactura >= @ini AND H.FechaFactura < @fin
      AND H.EnEspera = 0
      AND (@caja IS NULL OR H.CodCaja = @caja)
      AND (@noFiscal IS NULL OR H.NoFiscal = @noFiscal)
      AND (@credito IS NULL OR H.FacturaACredito = @credito)
    UNION ALL
    SELECT N.CodArticulo, NULL, -N.Cantidad,
           -(N.TotalLineaUsd - (ISNULL(N.DescuentoUSD,0) + ISNULL(N.MontoDescuentoFidelUsd,0) + ISNULL(N.MontoDescuentoFormPagoUsd,0))),
           -ISNULL(N.IvaUSD,0), -ISNULL(N.CostoUsd,0),
           -(N.TotalLinea - (ISNULL(N.MontoDescuento,0) + ISNULL(N.MontoDescuentoFidel,0) + ISNULL(N.MontoDescuentoFormPago,0))),
           -ISNULL(N.MontoImpuesto,0), -ISNULL(N.CostoBs,0)
    FROM KLK_NCREDITOHDR NH
    INNER JOIN KLK_NCREDITOLINE N ON N.NumNota = NH.NumNota AND N.CodCaja = NH.CodCaja AND {ordenIgual}
    WHERE NH.FechaNota >= @ini AND NH.FechaNota < @fin
      AND (@caja IS NULL OR NH.CodCaja = @caja){filtroNoFiscal}{filtroCredito}
),
PorArticulo AS (
    SELECT CodArticulo, SUM(Cantidad) AS Cantidad,
           SUM(BaseUsd) AS BaseUsd, SUM(IvaUsd) AS IvaUsd, SUM(CostoUsd) AS CostoUsd,
           SUM(BaseBs) AS BaseBs, SUM(IvaBs) AS IvaBs, SUM(CostoBs) AS CostoBs
    FROM Lineas
    GROUP BY CodArticulo
),
Stock AS (
    SELECT E.CodArticulo, SUM(E.Existencia) AS Existencia
    FROM KLK_SAP_EXISTENCIA E
    WHERE E.CodigoAlmacen IN (SELECT DISTINCT CodigoAlmacen FROM Lineas WHERE CodigoAlmacen IS NOT NULL)
    GROUP BY E.CodArticulo
)
SELECT ISNULL(NULLIF(LTRIM(RTRIM(A.NomGrpArticulo)), N''), N'SIN CATEGORÍA') AS Categoria,
       CAST(P.CodArticulo AS NVARCHAR(60))                AS Articulo,
       ISNULL(A.NomArticulo, N'(no está en el maestro)')  AS Descripcion,
       ISNULL(A.UMVenta, N'')                              AS Unidad,
       CAST(ISNULL(S.Existencia, 0) AS DECIMAL(18,2))      AS Existencia,
       CAST(P.Cantidad AS DECIMAL(18,2))                   AS Cantidad,
       CAST(P.BaseUsd AS DECIMAL(18,2))                    AS BaseUsd,
       CAST(P.IvaUsd AS DECIMAL(18,2))                     AS IvaUsd,
       CAST(P.CostoUsd AS DECIMAL(18,2))                   AS CostoUsd,
       CAST(P.BaseBs AS DECIMAL(18,2))                     AS BaseBs,
       CAST(P.IvaBs AS DECIMAL(18,2))                      AS IvaBs,
       CAST(P.CostoBs AS DECIMAL(18,2))                    AS CostoBs
FROM PorArticulo P
LEFT JOIN KLK_SAP_ARTICULO A ON A.CodArticulo = P.CodArticulo
LEFT JOIN Stock S ON S.CodArticulo = P.CodArticulo
OPTION (RECOMPILE);";
    }

    /// <param name="noFiscal">"" = todos, "0" = solo fiscales, "1" = solo no fiscales.</param>
    /// <param name="credito">"" = todos, "0" = solo contado, "1" = solo crédito.</param>
    public static async Task<List<object?[]>> EjecutarAsync(string cadena, DateTime desde, DateTime hasta, string caja,
                                                           string noFiscal, string credito, int timeoutSeg, CancellationToken ct)
    {
        var filas = new List<object?[]>();
        await using var conn = new SqlConnection(cadena);
        await conn.OpenAsync(ct);
        var nh = await ConsultaCierres.ColumnasAsync(conn, cadena, "KLK_NCREDITOHDR", timeoutSeg, ct);
        var nl = await ConsultaCierres.ColumnasAsync(conn, cadena, "KLK_NCREDITOLINE", timeoutSeg, ct);
        await using var cmd = new SqlCommand(Lectura.Prefijo + ConstruirSql(nh, nl), conn) { CommandTimeout = timeoutSeg };
        cmd.Parameters.AddWithValue("@p1", desde.Date);
        cmd.Parameters.AddWithValue("@p2", hasta.Date);
        cmd.Parameters.AddWithValue("@pCaja", caja ?? "");
        cmd.Parameters.AddWithValue("@pNoFiscal", noFiscal ?? "");
        cmd.Parameters.AddWithValue("@pCredito", credito ?? "");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var f = new object?[12];
            for (int i = 0; i < 4; i++) f[i] = rd.IsDBNull(i) ? "" : (Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture) ?? "").Trim();
            for (int i = 4; i < 12; i++) f[i] = rd.IsDBNull(i) ? 0m : Convert.ToDecimal(rd.GetValue(i), CultureInfo.InvariantCulture);
            filas.Add(f);
        }
        return filas;
    }
}
