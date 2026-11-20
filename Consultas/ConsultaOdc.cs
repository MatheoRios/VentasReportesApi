using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>
/// Órdenes de compra (Stellar): MA_ODC (encabezado) + TR_ODC (renglones: n_Cantidad pedida, n_Cant_Recibida recibida).
/// Igual que las consultas de impresión de la ODC: registros de la localidad de origen (cs_CodLocalidad, por defecto '00') y la
/// localidad de destino por el depósito a despachar (MA_ODC.c_Despachar → MA_DEPOSITO.c_CodLocalidad).
/// Si la base no tiene MA_ODC / TR_ODC, o algo falla, el reporte de compras sale igual sin las hojas de órdenes.
/// </summary>
public static class ConsultaOdc
{
    // Orden:   0 Documento, 1 Fecha de emisión, 2 Cód. proveedor, 3 Proveedor, 4 Estatus, 5 Localidad destino, 6 Moneda, 7 Fecha de vencimiento,
    //          8 Observación, 9 Tasa de la negociación, 10 Tipo de ODC (b_backorder = ORDEN ABIERTA), 11 Requisición, 12 Fecha de recepción,
    //          13 Comprador
    // Renglón: 0 Índice de la orden, 1 Línea, 2 Código, 3 Descripción, 4 Cant. pedida (unidades), 5 Cant. recibida, 6 Costo, 7 Unid. x empaque,
    //          8 Por peso (0/1), 9 Código de barras (MA_CODIGOS)
    // Recepción: 0 Índice de la orden, 1 N.º de recepción, 2 Fecha, 3 Código, 4 Cantidad recibida (unidades), 5 Diferencia contra lo facturado
    //          (TR_INVENTARIO concepto REC con c_Documento_origen = la ODC, cabecera MA_INVENTARIO en DPE / DCO, como la impresión de la recepción)
    private static readonly string[] Fechas = { "d_Fecha", "d_FechaEmision", "d_Fecha_Emision", "d_FechaDocumento", "f_Fecha", "Fecha" };
    private static readonly string[] Entregas = { "DU_FECHAVENCIMIENTO", "d_FechaVencimiento", "d_Fecha_Vencimiento", "d_FechaVence", "d_FechaEntrega", "d_Fecha_Entrega", "d_FechaRecepcion" };
    private static readonly string[] Tipos = { "c_TipoODC", "c_Tipo_ODC", "n_TipoODC", "cs_TipoODC", "c_TipoOrden", "n_TipoOrden", "c_Tipo", "n_Tipo" };

    private static string? Primera(Dictionary<string, string> t, params string[] cand)
    {
        var c = cand.FirstOrDefault(t.ContainsKey);
        return c == null ? null : t.Keys.First(k => string.Equals(k, c, StringComparison.OrdinalIgnoreCase));
    }
    private static string S(Dictionary<string, string> t, string a, params string[] cols)
    {
        var c = Primera(t, cols);
        return c == null ? "N''" : $"LTRIM(RTRIM(ISNULL(CONVERT(NVARCHAR(4000), {a}.[{c}]), N'')))";
    }
    private static string N(Dictionary<string, string> t, string a, params string[] cols)
    {
        var c = Primera(t, cols);
        return c == null ? "0" : ConsultaCierres.EsNumero(t[c]) ? $"ISNULL({a}.[{c}], 0)" : $"ISNULL(TRY_CONVERT(DECIMAL(19,6), {a}.[{c}]), 0)";
    }
    /// <summary>
    /// Nombre del usuario de Stellar (MA_USUARIOS.descripcion) para un código (comprador, ejecutor); si no está, queda el código.
    /// Solo se leen codusuario y descripcion: nunca las columnas de clave.
    /// </summary>
    internal static string Usuario(string expr, Dictionary<string, string>? us)
    {
        string limpio = $"NULLIF(NULLIF({expr}, N''), N'0')";
        if (us == null || !us.ContainsKey("codusuario") || !us.ContainsKey("descripcion")) return $"ISNULL({limpio}, N'')";
        return $"ISNULL(COALESCE((SELECT TOP 1 NULLIF(LTRIM(RTRIM(U.descripcion)), N'') FROM MA_USUARIOS U " +
               $"WHERE LTRIM(RTRIM(CONVERT(NVARCHAR(100), U.codusuario))) COLLATE DATABASE_DEFAULT = {limpio} COLLATE DATABASE_DEFAULT), {limpio}), N'')";
    }
    private static string R(string e) => $"CAST({e} AS DECIMAL(19,4))";

    private static string Sql(Dictionary<string, string> mo, Dictionary<string, string> tr, Dictionary<string, string> pr, Dictionary<string, string> dp,
                              Dictionary<string, string> mn, Dictionary<string, string> pd, Dictionary<string, string> cb, string fecha, int nLoc,
                              Dictionary<string, string>? us = null, Dictionary<string, string>? mi = null, Dictionary<string, string>? ti = null)
    {
            // recepciones (REC) de cada orden: lo que de verdad entró al depósito, renglón por renglón
            bool conRec = ti != null && mi != null && ti.ContainsKey("c_Documento_origen") && ti.ContainsKey("c_Concepto") && ti.ContainsKey("c_Documento")
                          && ti.ContainsKey("c_CodArticulo") && ti.ContainsKey("n_Cantidad") && mi.ContainsKey("c_Documento") && mi.ContainsKey("c_Concepto");
            string? fechaRec = conRec ? Primera(mi!, Fechas) : null;
            string recepciones = !conRec ? "" : $@"
SELECT O.Id, LTRIM(RTRIM(R.c_Documento)), {(fechaRec != null ? $"CONVERT(CHAR(19), MAX(H.[{fechaRec}]), 120)" : "N''")}, LTRIM(RTRIM(R.c_CodArticulo)),
       CAST(SUM(ISNULL(R.n_Cantidad, 0)) AS DECIMAL(19,4)),
       CAST(SUM({(ti!.ContainsKey("n_Cant_Diferencia") ? "ISNULL(R.n_Cant_Diferencia, 0)" : "0")}) AS DECIMAL(19,4))
FROM TR_INVENTARIO R
JOIN MA_INVENTARIO H ON H.c_Documento = R.c_Documento AND H.c_Concepto = R.c_Concepto
JOIN #O O ON O.Doc = R.c_Documento_origen COLLATE DATABASE_DEFAULT
WHERE R.c_Concepto = N'REC'{(mi!.ContainsKey("c_Status") ? " AND H.c_Status IN (N'DPE', N'DCO')" : "")}{(fechaRec != null ? $" AND H.[{fechaRec}] >= @desde" : "")}
GROUP BY O.Id, R.c_Documento, R.c_CodArticulo
ORDER BY O.Id, R.c_Documento;";
            // código de barras: MA_CODIGOS (c_CodNasa = código del producto, c_Codigo = barra); primero el de la unidad (n_Cantidad = 1),
            // y nunca el mismo código interno (c_codprincipal de TR_ODC es el código del producto, no la barra)
            bool conBarra = cb.ContainsKey("c_CodNasa") && cb.ContainsKey("c_Codigo");
            string barra = conBarra
                ? "ISNULL((SELECT TOP 1 LTRIM(RTRIM(Cb.c_Codigo)) FROM MA_CODIGOS Cb WHERE Cb.c_CodNasa = T.c_CodArticulo " +
                  "AND LTRIM(RTRIM(Cb.c_Codigo)) <> LTRIM(RTRIM(T.c_CodArticulo)) ORDER BY " +
                  (cb.ContainsKey("n_Cantidad") ? "CASE WHEN Cb.n_Cantidad = 1 THEN 0 ELSE 1 END, " : "") +
                  (cb.ContainsKey("ID") ? "Cb.ID" : "Cb.c_Codigo") + "), N'')"
                : "N''";
            bool conProv = mo.ContainsKey("c_CodProveedor") && pr.ContainsKey("c_CodProveed");
            bool conDep = mo.ContainsKey("c_Despachar") && dp.ContainsKey("c_CodDeposito") && dp.ContainsKey("c_CodLocalidad");
            bool conMon = mo.ContainsKey("c_CodMoneda") && mn.ContainsKey("c_CodMoneda");
            string? origenMo = Primera(mo, "cs_CodLocalidad"), origenTr = Primera(tr, "cs_CodLocalidad");
            string loc = conDep ? "LTRIM(RTRIM(CONVERT(NVARCHAR(50), Dp.c_CodLocalidad)))" : S(mo, "O", "c_CodLocalidad");
            string prov = conProv
                ? $"COALESCE(NULLIF(NULLIF({S(pr, "P", "c_Descripcio", "c_Descripcion")}, N''), N'0'), NULLIF(NULLIF({S(pr, "P", "c_razon")}, N''), N'0'), N'')"
                : "N''";
            string? entrega = Primera(mo, Entregas);
            string filtroLoc = nLoc == 0 ? "" : $" AND {loc} IN ({string.Join(", ", Enumerable.Range(0, nLoc).Select(i => "@l" + i))})";
            string porPeso = pd.ContainsKey("n_TipoPeso") ? "CASE WHEN Pd.n_TipoPeso = 1 THEN 1 ELSE 0 END" : "0";
            string descri = pd.ContainsKey("c_Descri") && tr.ContainsKey("c_Descripcion")
                ? "COALESCE(NULLIF(LTRIM(RTRIM(Pd.c_Descri)), N''), LTRIM(RTRIM(ISNULL(T.c_Descripcion, N''))))" : S(tr, "T", "c_Descripcion");

            return $@"
SET NOCOUNT ON;
DECLARE @desde DATETIME = @p1, @hasta DATETIME = DATEADD(DAY, 1, @p2);
CREATE TABLE #O (Id INT IDENTITY(0, 1) PRIMARY KEY, Doc NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL, Fecha NVARCHAR(30),
    CodProv NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Proveedor NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Estatus NVARCHAR(4000) COLLATE DATABASE_DEFAULT,
    Localidad NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Moneda NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Entrega NVARCHAR(30), Obs NVARCHAR(4000) COLLATE DATABASE_DEFAULT,
    Tasa DECIMAL(19,6), Tipo NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Requisicion NVARCHAR(4000) COLLATE DATABASE_DEFAULT,
    FechaRecepcion NVARCHAR(30), Comprador NVARCHAR(4000) COLLATE DATABASE_DEFAULT);
INSERT INTO #O (Doc, Fecha, CodProv, Proveedor, Estatus, Localidad, Moneda, Entrega, Obs, Tasa, Tipo, Requisicion, FechaRecepcion, Comprador)
SELECT LEFT(LTRIM(RTRIM(O.c_Documento)), 100), CONVERT(CHAR(19), O.[{fecha}], 120), {S(mo, "O", "c_CodProveedor")}, {prov}, {S(mo, "O", "c_Status")},
       {loc}, {(conMon ? S(mn, "M", "c_Descripcion") : S(mo, "O", "c_CodMoneda"))},
       {(entrega != null ? $"CONVERT(CHAR(19), O.[{entrega}], 120)" : "N''")},
       LEFT(REPLACE(REPLACE({S(mo, "O", "c_Observacion", "c_Descripcion", "c_Comentario")}, CHAR(13), N''), CHAR(10), N' · '), 4000)
       , CAST({N(mo, "O", "n_FactorCambio", "n_Factor_Cambio", "n_Tasa", "n_TasaCambio")} AS DECIMAL(19,6)),
       {(Primera(mo, "b_backorder") is string bo
            ? $"CASE WHEN CONVERT(NVARCHAR(10), O.[{bo}]) IN (N'1', N'True', N'S') THEN N'ORDEN ABIERTA' ELSE N'ORDEN CERRADA' END"   // backorder = acepta entregas parciales
            : S(mo, "O", Tipos))},
       {S(mo, "O", "cs_requisicion", "c_Requisicion")},
       {(Primera(mo, "d_fecha_recepcion", "d_FechaRecepcion") is string fr ? $"CONVERT(CHAR(19), O.[{fr}], 120)" : "N''")},
       {Usuario(S(mo, "O", "c_CodComprador", "c_Comprador"), us)}
FROM MA_ODC O
{(conProv ? "LEFT JOIN MA_PROVEEDORES P ON P.c_CodProveed = O.c_CodProveedor" : "")}
{(conDep ? "LEFT JOIN MA_DEPOSITO Dp ON Dp.c_CodDeposito = O.c_Despachar" : "")}
{(conMon ? "LEFT JOIN MA_MONEDAS M ON M.c_CodMoneda = O.c_CodMoneda" : "")}
WHERE O.[{fecha}] >= @desde AND O.[{fecha}] < @hasta{(origenMo != null ? $" AND O.[{origenMo}] = @origen" : "")}{filtroLoc}
ORDER BY O.[{fecha}], O.c_Documento;
CREATE INDEX IX_O ON #O (Doc);
SELECT Doc, Fecha, CodProv, Proveedor, Estatus, Localidad, Moneda, Entrega, Obs, Tasa, Tipo, Requisicion, FechaRecepcion, Comprador FROM #O ORDER BY Id;
SELECT O.Id, {(tr.ContainsKey("c_Linea") ? "LTRIM(RTRIM(CONVERT(NVARCHAR(20), T.c_Linea)))" : "N''")}, LTRIM(RTRIM(T.c_CodArticulo)), {descri},
       {R(N(tr, "T", "n_Cantidad"))}, {R(N(tr, "T", "n_Cant_Recibida", "n_CantRecibida"))}, {R(N(tr, "T", "n_Costo"))},
       {R(N(tr, "T", "ns_CantidadEmpaque"))}, {porPeso},
       {barra}
FROM #O O
JOIN TR_ODC T ON T.c_Documento = O.Doc{(origenTr != null ? $" AND T.[{origenTr}] = @origen" : "")}
LEFT JOIN MA_PRODUCTOS Pd ON Pd.c_Codigo = T.c_CodArticulo
ORDER BY O.Id{(tr.ContainsKey("c_Linea") ? ", T.c_Linea" : "")};{recepciones}
DROP TABLE #O;";
    }

    public static async Task<(List<object?[]> Ordenes, List<object?[]> Lineas, List<object?[]>? Recepciones, string? Aviso)> EjecutarAsync(
        SqlConnection conn, string cadena, DateTime desde, DateTime hasta, IReadOnlyList<string> localidades, string origen, int timeoutSeg, CancellationToken ct)
    {
        var ordenes = new List<object?[]>();
        var lineas = new List<object?[]>();
        List<object?[]>? recep = null;
        try
        {
            var mo = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_ODC", timeoutSeg, ct);
            var tr = await ConsultaCierres.ColumnasAsync(conn, cadena, "TR_ODC", timeoutSeg, ct);
            if (mo.Count == 0 || tr.Count == 0) return (ordenes, lineas, null, "La base no tiene MA_ODC / TR_ODC.");
            foreach (var col in new[] { "c_Documento" }) if (!mo.ContainsKey(col) || !tr.ContainsKey(col)) return (ordenes, lineas, null, "MA_ODC / TR_ODC sin c_Documento.");
            if (!tr.ContainsKey("n_Cantidad") || !tr.ContainsKey("c_CodArticulo")) return (ordenes, lineas, null, "TR_ODC sin n_Cantidad / c_CodArticulo.");
            string? fecha = Primera(mo, Fechas);
            if (fecha == null) return (ordenes, lineas, null, "No se encontró la columna de fecha de MA_ODC.");
            var pr = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_PROVEEDORES", timeoutSeg, ct);
            var dp = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_DEPOSITO", timeoutSeg, ct);
            var mn = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_MONEDAS", timeoutSeg, ct);
            var pd = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_PRODUCTOS", timeoutSeg, ct);

            var cb = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_CODIGOS", timeoutSeg, ct);
            var us = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_USUARIOS", timeoutSeg, ct);
            var mi = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_INVENTARIO", timeoutSeg, ct);
            var ti = await ConsultaCierres.ColumnasAsync(conn, cadena, "TR_INVENTARIO", timeoutSeg, ct);
            string sql = Sql(mo, tr, pr, dp, mn, pd, cb, fecha, localidades.Count, us, mi, ti);
            bool conRec = new[] { "c_Documento_origen", "c_Concepto", "c_Documento", "c_CodArticulo", "n_Cantidad" }.All(ti.ContainsKey)
                          && mi.ContainsKey("c_Documento") && mi.ContainsKey("c_Concepto");
            await using var cmd = new SqlCommand(Lectura.Prefijo + sql, conn) { CommandTimeout = timeoutSeg };
            cmd.Parameters.AddWithValue("@p1", desde.Date);
            cmd.Parameters.AddWithValue("@p2", hasta.Date);
            cmd.Parameters.AddWithValue("@origen", origen);
            for (int i = 0; i < localidades.Count; i++) cmd.Parameters.AddWithValue("@l" + i, localidades[i]);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await ConsultaCompras.Leer(rd, ordenes, ct);
            if (await rd.NextResultAsync(ct)) await ConsultaCompras.Leer(rd, lineas, ct);
            if (conRec && await rd.NextResultAsync(ct)) { recep = new List<object?[]>(); await ConsultaCompras.Leer(rd, recep, ct); }
            return (ordenes, lineas, recep, conRec ? null : "La base no tiene TR_INVENTARIO.c_Documento_origen: lo recibido se toma de las compras.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // las órdenes son un complemento: si fallan, las compras salen igual y el aviso dice por qué
            return (new List<object?[]>(), new List<object?[]>(), null, "No se pudieron leer las órdenes de compra: " + ex.Message);
        }
    }
}
