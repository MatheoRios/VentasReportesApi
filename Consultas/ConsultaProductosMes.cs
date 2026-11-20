using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>Resultado del Análisis de productos: ventas por artículo y mes + ficha (descripción, categoría, datos del maestro).</summary>
public sealed record ResultadoProductosMes(List<object?[]> Meses, List<object?[]> Articulos, string[] CamposFicha, List<string> Bloqueados, List<string> Inactivos);

/// <summary>
/// "Análisis de productos" (para decidir qué productos se migran): unidades y USD netos (facturas − notas de crédito)
/// por artículo y mes, más la ficha de cada artículo vendido. No usa existencias.
/// La ficha trae, además, los datos del maestro que sirven para el modelo de migración (código de barras, marca,
/// proveedor…). Como los nombres de columna de KLK_SAP_ARTICULO pueden variar, se buscan por nombre (sys.columns)
/// y lo que no exista sale vacío. "Gravable" se calcula de las ventas: si alguna línea llevó IVA.
/// </summary>
public static class ConsultaProductosMes
{
    private const string SqlLineas = @"
SET NOCOUNT ON;
DECLARE @ini DATETIME = @p1, @fin DATETIME = DATEADD(DAY, 1, @p2);

CREATE TABLE #L (CodArticulo NVARCHAR(60) NOT NULL, Mes DATE NOT NULL, Cantidad DECIMAL(18,3), Usd DECIMAL(18,4), IvaUsd DECIMAL(18,4),
                 Fecha DATE NULL, EsNC BIT NOT NULL, Factura NVARCHAR(80) NULL, PrecioU DECIMAL(18,4) NULL);

INSERT INTO #L
SELECT CAST(F.CodArticulo AS NVARCHAR(60)), DATEFROMPARTS(YEAR(H.FechaFactura), MONTH(H.FechaFactura), 1), F.Cantidad,
       F.TotalLineaUsd + ISNULL(F.IvaUSD,0) - (ISNULL(F.DescuentoUSD,0) + ISNULL(F.MontoDescuentoFidelUsd,0) + ISNULL(F.MontoDescuentoFormPagoUsd,0)),
       ISNULL(F.IvaUSD,0), CAST(H.FechaFactura AS DATE), 0,
       CONVERT(NVARCHAR(30), H.CodCaja) + N'|' + CONVERT(NVARCHAR(40), H.NumFactura),
       -- precio unitario en USD sin IVA ni descuentos (para comparar el precio entre tiendas)
       CASE WHEN F.Cantidad > 0 THEN CAST(F.TotalLineaUsd / F.Cantidad AS DECIMAL(18,4)) END
FROM KLK_FACTURAHDR H
INNER JOIN KLK_FACTURALINE F ON F.NumFactura = H.NumFactura AND F.CodCaja = H.CodCaja AND F.CodApertura = H.CodApertura
WHERE H.FechaFactura >= @ini AND H.FechaFactura < @fin AND H.EnEspera = 0;

INSERT INTO #L
SELECT CAST(N.CodArticulo AS NVARCHAR(60)), DATEFROMPARTS(YEAR(NH.FechaNota), MONTH(NH.FechaNota), 1), -N.Cantidad,
       -(N.TotalLineaUsd + ISNULL(N.IvaUSD,0) - (ISNULL(N.DescuentoUSD,0) + ISNULL(N.MontoDescuentoFidelUsd,0) + ISNULL(N.MontoDescuentoFormPagoUsd,0))),
       0, CAST(NH.FechaNota AS DATE), 1, NULL, NULL
FROM KLK_NCREDITOHDR NH
INNER JOIN KLK_NCREDITOLINE N ON N.NumNota = NH.NumNota AND N.CodCaja = NH.CodCaja AND N.Orden = NH.CodApertura
WHERE NH.FechaNota >= @ini AND NH.FechaNota < @fin;

-- 1) ventas por artículo y mes
SELECT CodArticulo, CONVERT(CHAR(7), Mes, 126), CAST(SUM(Cantidad) AS DECIMAL(18,3)), CAST(SUM(Usd) AS DECIMAL(18,2))
FROM #L
GROUP BY CodArticulo, Mes;
";

    // campo del modelo → columnas posibles en KLK_SAP_ARTICULO (la primera que exista)
    public static readonly (string Campo, string[] Columnas)[] CamposMaestro =
    {
        ("CodigoBarras", new[] { "CodBarra", "CodBarras", "CodigoBarra", "CodigoBarras", "CodBarraPrincipal", "EAN", "CodigoEAN", "CodeBars", "Barcode" }),
        ("Marca",        new[] { "NomMarca", "Marca", "DesMarca", "NombreMarca", "NomFabricante", "Fabricante", "FirmName" }),
        ("Proveedor",    new[] { "NomProveedor", "Proveedor", "NombreProveedor", "NomProveedorPref", "CardName" }),
        ("RifProveedor", new[] { "RifProveedor", "RIFProveedor", "CodProveedor", "CodigoProveedor", "CardCode" }),
        ("UdMCompra",    new[] { "UMCompra", "UnidadCompra", "UMCompras", "BuyUnitMsr" }),
        ("UndBulto",     new[] { "FactorUMCompra", "UndXBulto", "UnidXBulto", "UnidadesXBulto", "CantXBulto", "CantidadXBulto", "NumEnCompra", "NumInBuy", "Empaque" }),
        ("PesoKg",       new[] { "PesoKg", "Peso", "PesoVenta", "SWeight1" }),
        ("Volumen",      new[] { "Volumen", "VolumenVenta", "SVolume" }),
        ("Exento",       new[] { "Exento" }),
        ("Impuesto",     new[] { "Impuesto" }),
        ("Pesable",      new[] { "Pesable" }),
        ("Tipo",         new[] { "Tipo" }),
        ("ArtInv",       new[] { "ArtInv" }),
        ("ArtVent",      new[] { "ArtVent" }),
        ("ArtCompra",    new[] { "ArtCompra" }),
        ("Bloqueado",    new[] { "Bloqueado" }),
        ("Estatus",      new[] { "Estatus" }),
        ("Departamento", new[] { "Departamento" }),
        ("Subgrupo",     new[] { "Subgrupo" }),
        ("PrecioMaestro", new[] { "PrecioVentaUsd", "PrecioUsd", "PrecioDolar", "PrecioDivisa", "PrecioVenta", "PrecioDetal", "Precio1", "Precio", "PVP" }),
    };

    public static async Task<ResultadoProductosMes> EjecutarAsync(string cadena, DateTime desde, DateTime hasta, int timeoutSeg, CancellationToken ct)
    {
        var meses = new List<object?[]>();
        var arts = new List<object?[]>();
        await using var conn = new SqlConnection(cadena);
        await conn.OpenAsync(ct);

        // columnas del maestro (una vez por conexión: es una consulta muy liviana)
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var c = new SqlCommand("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.KLK_SAP_ARTICULO')", conn) { CommandTimeout = 30 })
        await using (var r = await c.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) cols.Add(Convert.ToString(r.GetValue(0))!);
        var extras = CamposMaestro.Select(cm =>
        {
            var col = cm.Columnas.FirstOrDefault(cols.Contains);
            return col == null ? "CAST(NULL AS NVARCHAR(100))" : $"CAST(A.[{col}] AS NVARCHAR(100))";
        });

        string bloqueados = cols.Contains("Bloqueado")
            ? "SELECT CAST(CodArticulo AS NVARCHAR(60)) FROM KLK_SAP_ARTICULO WHERE CONVERT(NVARCHAR(10), Bloqueado) IN (N'1', N'S', N'SI', N'True', N'Y');"
            : "SELECT TOP 0 N'';";
        string inactivos = cols.Contains("Estatus")
            ? "SELECT CAST(CodArticulo AS NVARCHAR(60)) FROM KLK_SAP_ARTICULO WHERE CONVERT(NVARCHAR(10), Estatus) IN (N'1', N'I', N'INACTIVO', N'True');"
            : "SELECT TOP 0 N'';";
        string sql = Lectura.Prefijo + SqlLineas + $@"
-- 2) ficha de los artículos vendidos (las existencias no se usan) + gravable según las ventas + datos del maestro
SELECT X.CodArticulo,
       ISNULL(A.NomArticulo, N'(no está en el maestro)'),
       ISNULL(NULLIF(LTRIM(RTRIM(A.NomGrpArticulo)), N''), N'SIN CATEGORÍA'),
       ISNULL(A.UMVenta, N''),
       CAST(0 AS DECIMAL(18,2)),
       X.ConIva,
       {string.Join(",\n       ", extras)},
       CAST(X.LineasFac AS NVARCHAR(20)), CAST(X.LineasIva AS NVARCHAR(20)),
       ISNULL(CONVERT(NVARCHAR(10), X.UltIva, 23), N''), ISNULL(CONVERT(NVARCHAR(10), X.UltSinIva, 23), N''),
       ISNULL(X.EjIva, N''), ISNULL(X.EjSinIva, N''), ISNULL(CONVERT(NVARCHAR(10), X.UltVenta, 23), N''),
       ISNULL(CONVERT(NVARCHAR(30), U.PrecioU), N''), ISNULL(CONVERT(NVARCHAR(10), U.Fecha, 23), N'')
FROM (SELECT CodArticulo, MAX(CASE WHEN IvaUsd > 0 THEN 1 ELSE 0 END) AS ConIva,
             -- facturas distintas (no líneas)
             COUNT(DISTINCT CASE WHEN EsNC = 0 THEN Factura END) AS LineasFac,
             COUNT(DISTINCT CASE WHEN EsNC = 0 AND IvaUsd > 0 THEN Factura END) AS LineasIva,
             MAX(CASE WHEN EsNC = 0 AND IvaUsd > 0 THEN Fecha END) AS UltIva,
             MAX(CASE WHEN EsNC = 0 AND IvaUsd <= 0 THEN Fecha END) AS UltSinIva,
             -- última factura de cada tipo: aaaa-mm-dd|caja|número
             MAX(CASE WHEN EsNC = 0 AND IvaUsd > 0 THEN CONVERT(NVARCHAR(10), Fecha, 23) + N'|' + Factura END) AS EjIva,
             MAX(CASE WHEN EsNC = 0 AND IvaUsd <= 0 THEN CONVERT(NVARCHAR(10), Fecha, 23) + N'|' + Factura END) AS EjSinIva,
             MAX(CASE WHEN EsNC = 0 THEN Fecha END) AS UltVenta
      FROM #L GROUP BY CodArticulo) X
-- precio de la última venta de cada artículo en esta tienda
LEFT JOIN (SELECT CodArticulo, PrecioU, Fecha,
                  ROW_NUMBER() OVER (PARTITION BY CodArticulo ORDER BY Fecha DESC, Factura DESC) AS Rn
           FROM #L WHERE EsNC = 0 AND PrecioU IS NOT NULL) U ON U.CodArticulo = X.CodArticulo AND U.Rn = 1
LEFT JOIN KLK_SAP_ARTICULO A ON CAST(A.CodArticulo AS NVARCHAR(60)) = X.CodArticulo;

-- 3) artículos bloqueados en el maestro de esta tienda (aunque no se hayan vendido)
{bloqueados}

-- 4) artículos inactivos (Estatus = 1) en el maestro de esta tienda (aunque no se hayan vendido)
{inactivos}

DROP TABLE #L;";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeoutSeg };
        cmd.Parameters.AddWithValue("@p1", desde.Date);
        cmd.Parameters.AddWithValue("@p2", hasta.Date);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            meses.Add(new object?[]
            {
                Convert.ToString(rd.GetValue(0))!.Trim(), Convert.ToString(rd.GetValue(1))!.Trim(),
                rd.IsDBNull(2) ? 0m : Convert.ToDecimal(rd.GetValue(2)), rd.IsDBNull(3) ? 0m : Convert.ToDecimal(rd.GetValue(3))
            });
        if (await rd.NextResultAsync(ct))
            while (await rd.ReadAsync(ct))
            {
                var f = new object?[rd.FieldCount];
                for (int i = 0; i < 4; i++) f[i] = rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i))!.Trim();
                f[4] = rd.IsDBNull(4) ? 0m : Convert.ToDecimal(rd.GetValue(4));
                f[5] = rd.IsDBNull(5) ? 0 : Convert.ToInt32(rd.GetValue(5));
                for (int i = 6; i < rd.FieldCount; i++) f[i] = rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i))!.Trim();
                arts.Add(f);
            }
        var bloq = new List<string>();
        if (await rd.NextResultAsync(ct))
            while (await rd.ReadAsync(ct))
                if (!rd.IsDBNull(0)) bloq.Add(Convert.ToString(rd.GetValue(0))!.Trim());
        var inac = new List<string>();
        if (await rd.NextResultAsync(ct))
            while (await rd.ReadAsync(ct))
                if (!rd.IsDBNull(0)) inac.Add(Convert.ToString(rd.GetValue(0))!.Trim());
        // los campos que empiezan con "_" no son del maestro: estadística de lo facturado en esta tienda (IVA, último precio)
        return new ResultadoProductosMes(meses, arts,
            CamposMaestro.Select(c => c.Campo).Concat(new[] { "_LineasFac", "_LineasIva", "_UltIva", "_UltSinIva", "_EjIva", "_EjSinIva", "_UltVenta", "_PrecioUlt", "_FechaPrecio" }).ToArray(),
            bloq, inac);
    }
}
