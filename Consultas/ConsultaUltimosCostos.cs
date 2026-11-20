using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>
/// "Últimos costos de compra" (base central Stellar, la misma de Compras): una fila por producto comprado en el período,
/// con su ÚLTIMA compra (costo, fecha, documento, proveedor, localidad, moneda y tasa), la penúltima (para ver si subió o bajó),
/// lo comprado en el período (veces, costo mínimo, máximo y promedio ponderado) y los costos y el precio de la ficha.
/// Solo compras procesadas (MA_COMPRAS: c_Concepto = 'COM', c_Status = 'DCO') y renglones con costo mayor que 0.
/// </summary>
public static class ConsultaUltimosCostos
{
    // Fila: 0 Código, 1 Descripción, 2 Departamento, 3 Presentación, 4 Por peso (0/1),
    //       5 Último costo, 6 Fecha "yyyy-MM-dd HH:mm:ss", 7 Documento, 8 Cód. proveedor, 9 Proveedor, 10 Localidad, 11 Moneda, 12 Tasa del documento,
    //       13 Cantidad de la última compra, 14 Penúltimo costo, 15 Fecha penúltima, 16 Proveedor penúltimo,
    //       17 Compras en el período, 18 Costo mínimo, 19 Costo máximo, 20 Costo promedio (ponderado por cantidad), 21 Cantidad comprada,
    //       22 Costo actual (ficha), 23 Costo de reposición (ficha), 24 Precio 1 (ficha),
    //       25 Recepción (n.º en la observación de la compra), 26 Orden de compra (observación «ODC …» o c_RELACION),
    //       27 Costo en la orden de compra (TR_ODC.n_Costo de ese producto; 0 si no hay orden o el producto no estaba en ella)
    private static readonly string[] LocalidadPosibles = { "c_CodLocalidad", "c_Localidad", "c_Sucursal", "c_CodSucursal" };
    private static readonly string[] FechasPosibles =
        { "d_Fecha", "d_FechaRecepcion", "d_Fecha_Recepcion", "d_FechaDocumento", "d_Fecha_Documento", "d_FechaEmision", "f_Fecha", "Fecha" };

    private static string? Primera(Dictionary<string, string> t, params string[] candidatas)
    {
        var c = candidatas.FirstOrDefault(t.ContainsKey);
        return c == null ? null : t.Keys.First(k => string.Equals(k, c, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sql(Dictionary<string, string> c, Dictionary<string, string> pr, Dictionary<string, string> mo, Dictionary<string, string> ti,
                              Dictionary<string, string> pd, Dictionary<string, string> dt, string colFecha, string? colLoc, string? colDep, int nLoc,
                              bool soloRec = false)
    {
        string N(Dictionary<string, string> t, string a, params string[] cols)
        {
            var col = Primera(t, cols);
            return col == null ? "0" : ConsultaCierres.EsNumero(t[col]) ? $"ISNULL({a}.[{col}], 0)" : $"ISNULL(TRY_CONVERT(DECIMAL(19,6), {a}.[{col}]), 0)";
        }
        string S(Dictionary<string, string> t, string a, params string[] cols)
        {
            var col = Primera(t, cols);
            return col == null ? "N''" : $"LTRIM(RTRIM(ISNULL(CONVERT(NVARCHAR(200), {a}.[{col}]), N'')))";
        }
        static string R(string e) => $"CAST({e} AS DECIMAL(19,4))";
        bool conProv = pr.ContainsKey("c_CodProveed") && c.ContainsKey("c_CodProveedor");
        bool conMoneda = mo.ContainsKey("c_codmoneda") && c.ContainsKey("c_codmoneda");
        string Limpio(string e) => $"NULLIF(NULLIF({e}, N''), N'0')";
        string prov = !conProv ? "N''"
            : $"COALESCE({Limpio(S(pr, "P", "c_Descripcio", "c_Descripcion"))}, {Limpio(S(pr, "P", "c_razon", "c_RazonSocial"))}, N'')";
        string moneda = conMoneda ? S(mo, "M", "c_descripcion") : S(c, "C", "c_codmoneda");
        string factorMon = conMoneda ? $"NULLIF({N(mo, "M", "n_factor")}, 0)" : "NULL";
        string factor = c.ContainsKey("n_FactorCambio") ? $"COALESCE(NULLIF({N(c, "C", "n_FactorCambio")}, 0), {factorMon}, 1)" : $"COALESCE({factorMon}, 1)";
        string status = c.ContainsKey("c_Status") ? " AND C.c_Status = N'DCO'" : "";
        string? exprLoc = colLoc != null ? $"LTRIM(RTRIM(CONVERT(NVARCHAR(50), C.[{colLoc}])))"
                        : colDep != null ? "LTRIM(RTRIM(CONVERT(NVARCHAR(50), Dp.c_CodLocalidad)))" : null;
        string joinDep = colLoc == null && colDep != null ? $"LEFT JOIN MA_DEPOSITO Dp ON Dp.c_CodDeposito = C.[{colDep}]" : "";
        string filtroLoc = exprLoc == null || nLoc == 0 ? "" : $" AND {exprLoc} IN ({string.Join(", ", Enumerable.Range(0, nLoc).Select(i => "@l" + i))})";
        string cantidad = N(ti, "I", "n_cant_teorica", "n_Cantidad");
        string obs = S(c, "C", "c_Observacion", "c_Descripcion", "c_Comentario").Replace("NVARCHAR(200)", "NVARCHAR(4000)");
        // ficha del producto
        string descri = pd.ContainsKey("c_Descri") ? "COALESCE(NULLIF(LTRIM(RTRIM(Pd.c_Descri)), N''), U.Descri)" : "U.Descri";
        string porPeso = pd.ContainsKey("n_TipoPeso") ? "CASE WHEN Pd.n_TipoPeso = 1 THEN 1 ELSE 0 END" : "0";
        string? colDepto = Primera(pd, "c_Departamento", "c_CodDepartamento", "c_Grupo");
        bool conDeptos = colDepto != null && dt.ContainsKey("c_codigo") && (dt.ContainsKey("c_descripcio") || dt.ContainsKey("c_descripcion"));
        string codDepto = colDepto == null ? "N''" : $"LTRIM(RTRIM(CONVERT(NVARCHAR(50), Pd.[{colDepto}])))";
        string depto = !conDeptos ? codDepto
            : $"COALESCE(NULLIF((SELECT TOP 1 LTRIM(RTRIM(Dt.[{(dt.ContainsKey("c_descripcio") ? "c_descripcio" : "c_descripcion")}])) FROM MA_DEPARTAMENTOS Dt WHERE Dt.c_codigo = Pd.[{colDepto}]), N''), {codDepto})";

        return $@"
SET NOCOUNT ON;
DECLARE @desde DATETIME = @p1, @hasta DATETIME = DATEADD(DAY, 1, @p2);
CREATE TABLE #C (Doc NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY, Fecha DATETIME, CodProv NVARCHAR(200) COLLATE DATABASE_DEFAULT,
    Proveedor NVARCHAR(200) COLLATE DATABASE_DEFAULT, Localidad NVARCHAR(50) COLLATE DATABASE_DEFAULT, Moneda NVARCHAR(200) COLLATE DATABASE_DEFAULT,
    Factor DECIMAL(19,6), Obs NVARCHAR(400) COLLATE DATABASE_DEFAULT, Relacion NVARCHAR(100) COLLATE DATABASE_DEFAULT);
INSERT INTO #C (Doc, Fecha, CodProv, Proveedor, Localidad, Moneda, Factor, Obs, Relacion)
SELECT LEFT(LTRIM(RTRIM(C.c_Documento)), 100), MAX(C.[{colFecha}]), MAX({S(c, "C", "c_CodProveedor")}), MAX({prov}), MAX({exprLoc ?? "N''"}),
       MAX({moneda}), MAX(CAST({factor} AS DECIMAL(19,6))), MAX(LEFT({obs}, 400)), MAX(LEFT({S(c, "C", "c_RELACION", "c_Relacion")}, 100))
FROM MA_COMPRAS C
{(conProv ? "LEFT JOIN MA_PROVEEDORES P ON C.c_CodProveedor = P.c_CodProveed" : "")}
{(conMoneda ? "LEFT JOIN MA_MONEDAS M ON M.c_codmoneda = C.c_codmoneda" : "")}
{joinDep}
WHERE C.c_Concepto = N'COM'{status}{filtroLoc}{(soloRec ? $" AND {obs} LIKE N'%RECEPCI%N%'" : "")} AND C.[{colFecha}] >= @desde AND C.[{colFecha}] < @hasta
GROUP BY LEFT(LTRIM(RTRIM(C.c_Documento)), 100);

-- cada renglón con costo, numerado por producto de la compra más reciente a la más antigua
SELECT LTRIM(RTRIM(I.c_CodArticulo)) COLLATE DATABASE_DEFAULT AS Cod,
       {(ti.ContainsKey("c_Descripcion") ? "LTRIM(RTRIM(ISNULL(I.c_Descripcion, N'')))" : "N''")} AS Descri,
       C.Fecha, C.Doc, C.CodProv, C.Proveedor, C.Localidad, C.Moneda, C.Factor, C.Obs, C.Relacion,
       CAST({N(ti, "I", "n_Costo")} AS DECIMAL(19,6)) AS Costo, CAST({cantidad} AS DECIMAL(19,6)) AS Cant,
       ROW_NUMBER() OVER (PARTITION BY LTRIM(RTRIM(I.c_CodArticulo)) ORDER BY C.Fecha DESC, C.Doc DESC) AS Rn
INTO #L
FROM #C C
JOIN TR_INVENTARIO I ON I.c_Documento = C.Doc AND I.c_Concepto = N'COM'
WHERE {N(ti, "I", "n_Costo")} > 0;
CREATE INDEX IX_L ON #L (Cod, Rn);

SELECT U.Cod, {descri}, {depto}, {S(pd, "Pd", "c_Presenta")}, {porPeso},
       {R("U.Costo")}, CONVERT(CHAR(19), U.Fecha, 120), U.Doc, U.CodProv, U.Proveedor, U.Localidad, U.Moneda, CAST(U.Factor AS DECIMAL(19,6)), {R("U.Cant")},
       {R("ISNULL(P2.Costo, 0)")}, ISNULL(CONVERT(CHAR(19), P2.Fecha, 120), N''), ISNULL(P2.Proveedor, N''),
       S.Compras, {R("S.CostoMin")}, {R("S.CostoMax")}, {R("S.CostoProm")}, {R("S.Cant")},
       {R(N(pd, "Pd", "n_CostoAct", "n_Costo_Act", "n_CostoActual"))}, {R(N(pd, "Pd", "n_Costorep", "n_CostoRep"))}, {R(N(pd, "Pd", "n_Precio1"))},
       ISNULL(U.Obs, N''), ISNULL(U.Relacion, N'')
FROM #L U
LEFT JOIN #L P2 ON P2.Cod = U.Cod AND P2.Rn = 2
JOIN (SELECT Cod, COUNT(*) AS Compras, MIN(Costo) AS CostoMin, MAX(Costo) AS CostoMax,
             CASE WHEN SUM(Cant) > 0 THEN SUM(Costo * Cant) / SUM(Cant) ELSE AVG(Costo) END AS CostoProm, SUM(Cant) AS Cant
      FROM #L GROUP BY Cod) S ON S.Cod = U.Cod
LEFT JOIN MA_PRODUCTOS Pd ON Pd.c_Codigo = U.Cod
WHERE U.Rn = 1
ORDER BY 2;
DROP TABLE #L; DROP TABLE #C;";
    }

    private static readonly System.Text.RegularExpressions.Regex ReOdc =
        new(@"\bODC\s*(?:N[°º.]?\s*)?(\d{3,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex ReRecepcion =
        new(@"RECEPCI[OÓ]N\s*(?:No\.?|N[°º])?\s*(\d{3,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static async Task<List<object?[]>> EjecutarAsync(string cadena, DateTime desde, DateTime hasta, IReadOnlyList<string> localidades,
                                                            int timeoutSeg, CancellationToken ct, bool soloConRecepcion = false, string origenOdc = "00")
    {
        await using var conn = new SqlConnection(cadena);
        await conn.OpenAsync(ct);
        var c = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_COMPRAS", timeoutSeg, ct);
        var ti = await ConsultaCierres.ColumnasAsync(conn, cadena, "TR_INVENTARIO", timeoutSeg, ct);
        if (c.Count == 0 || ti.Count == 0) throw new InvalidOperationException("La base no tiene las tablas MA_COMPRAS / TR_INVENTARIO (¿es la base central VAD10?).");
        foreach (var (t, cols, col) in new[] { ("MA_COMPRAS", c, "c_Documento"), ("MA_COMPRAS", c, "c_Concepto"), ("TR_INVENTARIO", ti, "c_Documento"),
                                               ("TR_INVENTARIO", ti, "c_Concepto"), ("TR_INVENTARIO", ti, "c_CodArticulo"), ("TR_INVENTARIO", ti, "n_Costo") })
            if (!cols.ContainsKey(col)) throw new InvalidOperationException($"La tabla {t} no tiene la columna {col}.");
        var pr = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_PROVEEDORES", timeoutSeg, ct);
        var mo = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_MONEDAS", timeoutSeg, ct);
        var pd = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_PRODUCTOS", timeoutSeg, ct);
        var dt = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_DEPARTAMENTOS", timeoutSeg, ct);
        string fecha = Primera(c, FechasPosibles) ?? throw new InvalidOperationException("No se encontró la columna de fecha de MA_COMPRAS.");
        string? loc = Primera(c, LocalidadPosibles);
        string? dep = null;
        if (loc == null && c.ContainsKey("c_CodDeposito"))
        {
            var dp = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_DEPOSITO", timeoutSeg, ct);
            if (dp.ContainsKey("c_CodDeposito") && dp.ContainsKey("c_CodLocalidad")) dep = "c_CodDeposito";
        }
        if (loc == null && dep == null && localidades.Count > 0)
            throw new InvalidOperationException("MA_COMPRAS no tiene la columna de localidad: no se puede filtrar por localidad.");

        var filas = new List<object?[]>();
        await using (var cmd = new SqlCommand(Lectura.Prefijo + Sql(c, pr, mo, ti, pd, dt, fecha, loc, dep, localidades.Count, soloConRecepcion), conn)
                     { CommandTimeout = timeoutSeg })
        {
            cmd.Parameters.AddWithValue("@p1", desde.Date);
            cmd.Parameters.AddWithValue("@p2", hasta.Date);
            for (int i = 0; i < localidades.Count; i++) cmd.Parameters.AddWithValue("@l" + i, localidades[i]);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await ConsultaCompras.Leer(rd, filas, ct);
        }

        // recepción y orden de compra de la última compra (de la observación, como en Compras) y el costo pactado en esa orden
        var pares = new List<(int Fila, long Odc, string Cod)>();
        for (int i = 0; i < filas.Count; i++)
        {
            var f = filas[i];
            string o = Convert.ToString(f[25]) ?? "", rel = (Convert.ToString(f[26]) ?? "").Trim();
            var mr = ReRecepcion.Match(o);
            var mo2 = ReOdc.Match(o);
            string odc = mo2.Success ? mo2.Groups[1].Value : (rel.Length >= 3 && rel.All(char.IsDigit) && rel.Trim('0') != "" ? rel : "");
            var g = new object?[28];
            Array.Copy(f, g, 25);
            g[25] = mr.Success ? mr.Groups[1].Value : ""; g[26] = odc; g[27] = 0m;
            filas[i] = g;
            if (odc != "" && long.TryParse(odc, out long n)) pares.Add((i, n, Convert.ToString(f[0]) ?? ""));
        }
        if (pares.Count > 0)
        {
            try
            {
                var tr = await ConsultaCierres.ColumnasAsync(conn, cadena, "TR_ODC", timeoutSeg, ct);
                if (tr.ContainsKey("c_Documento") && tr.ContainsKey("c_CodArticulo") && tr.ContainsKey("n_Costo"))
                {
                    var sb = new System.Text.StringBuilder("SET NOCOUNT ON;\nCREATE TABLE #O (Num BIGINT, Cod NVARCHAR(50) COLLATE DATABASE_DEFAULT);\n");
                    foreach (var lote in pares.Select(x => (x.Odc, x.Cod)).Distinct().Chunk(900))
                        sb.Append("INSERT INTO #O VALUES ").Append(string.Join(",", lote.Select(x => $"({x.Odc},N'{x.Cod.Replace("'", "''")}')"))).Append(";\n");
                    string origen = tr.ContainsKey("cs_CodLocalidad") ? " AND T.cs_CodLocalidad = @origen" : "";
                    sb.Append($@"SELECT O.Num, O.Cod, CAST(MAX(T.n_Costo) AS DECIMAL(19,4))
FROM #O O JOIN TR_ODC T ON TRY_CONVERT(BIGINT, T.c_Documento) = O.Num AND LTRIM(RTRIM(T.c_CodArticulo)) = O.Cod{origen}
GROUP BY O.Num, O.Cod;
DROP TABLE #O;");
                    var costos = new Dictionary<(long, string), decimal>();
                    await using var cmd2 = new SqlCommand(Lectura.Prefijo + sb, conn) { CommandTimeout = timeoutSeg };
                    cmd2.Parameters.AddWithValue("@origen", origenOdc);
                    await using var rd2 = await cmd2.ExecuteReaderAsync(ct);
                    while (await rd2.ReadAsync(ct))
                        costos[(Convert.ToInt64(rd2.GetValue(0)), Convert.ToString(rd2.GetValue(1)) ?? "")] = rd2.IsDBNull(2) ? 0m : Convert.ToDecimal(rd2.GetValue(2));
                    foreach (var p in pares)
                        if (costos.TryGetValue((p.Odc, p.Cod), out var cto)) filas[p.Fila][27] = cto;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* sin el costo de la orden el reporte sale igual */ }
        }
        return filas;
    }
}
