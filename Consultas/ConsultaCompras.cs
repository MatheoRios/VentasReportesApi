using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>Resultado del reporte de compras: documentos (facturas de compra) y sus renglones (apuntan al documento por índice).</summary>
public sealed record ResultadoCompras(List<object?[]> Documentos, List<object?[]> Lineas, string ColumnaFecha,
                                      List<object?[]> Ordenes, List<object?[]> LineasOdc, string? AvisoOdc,
                                      List<object?[]>? RecepcionesOdc = null);

/// <summary>
/// "Compras de productos" (base central Stellar, p. ej. VAD10 en 192.168.20.3):
///   Documentos: MA_COMPRAS (c_Concepto = 'COM' compras y 'DCM' devoluciones a proveedor, c_Status = 'DCO') + MA_PROVEEDORES + MA_MONEDAS.
///   Renglones:  TR_INVENTARIO (mismo c_Documento y mismo c_Concepto) + MA_PRODUCTOS.
///   Localidad:  MA_COMPRAS.c_CodLocalidad; si no existe, la del depósito (MA_COMPRAS.c_CodDeposito → MA_DEPOSITO.c_CodLocalidad).
/// Son las mismas consultas del registro de factura de compra, pero por rango de fechas en vez de un documento.
/// Varias columnas cambian de nombre entre versiones: se leen las columnas de cada tabla y se usa la primera que exista
/// (lo que no exista sale vacío / 0). La conexión va en Reportes:ConexionCompras (variable Reportes__ConexionCompras).
/// </summary>
public static class ConsultaCompras
{
    // Documento: 0 Documento, 1 Fecha "yyyy-MM-dd HH:mm:ss", 2 Cód. proveedor, 3 Proveedor, 4 RIF, 5 Factura del proveedor,
    //            6 Moneda, 7 Símbolo, 8 Factor moneda, 9 Subtotal, 10 Descuento, 11 Impuesto, 12 Total, 13 Localidad, 14 Usuario, 15 Observación,
    //            16 Concepto (COM = compra, DCM = devolución a proveedor; montos tal como están en la base, positivos),
    //            17 N.º de control, 18 Fecha de emisión de la factura, 19 Relación (c_RELACION), 20 Ejecutor, 21 Retención, 22 Base imponible
    //            (8 Factor = tasa del documento: MA_COMPRAS.n_FACTORCAMBIO; si no tiene, el factor actual de la moneda)
    // Renglón:   0 Índice del documento, 1 Línea, 2 Código, 3 Descripción, 4 Cantidad, 5 Empaques, 6 Kilos, 7 Unidades por empaque,
    //            8 Costo, 9 Precio, 10 Precio original, 11 Desc. específico, 12 Desc. general, 13 Subtotal, 14 Impuesto, 15 Total,
    //            16 % impuesto, 17 Costo de reposición (ficha), 18 Precio 1 (ficha), 19 Presentación, 20 Por peso (0/1), 21 Departamento,
    //            22 Tiene ficha en MA_PRODUCTOS (0/1)
    private static readonly string[] LocalidadPosibles = { "c_CodLocalidad", "c_Localidad", "c_Sucursal", "c_CodSucursal" };
    private static readonly string[] FechasPosibles =
        { "d_Fecha", "d_FechaRecepcion", "d_Fecha_Recepcion", "d_FechaDocumento", "d_Fecha_Documento", "d_FechaEmision", "f_Fecha", "Fecha" };

    /// <summary>Nombre REAL (con sus mayúsculas) de la primera columna candidata que exista en la tabla.</summary>
    private static string? Primera(Dictionary<string, string> t, params string[] candidatas)
    {
        var c = candidatas.FirstOrDefault(t.ContainsKey);
        return c == null ? null : t.Keys.First(k => string.Equals(k, c, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sql(Dictionary<string, string> c, Dictionary<string, string> pr, Dictionary<string, string> mo,
                              Dictionary<string, string> ti, Dictionary<string, string> pd, string colFecha, string? colLoc, int nLoc,
                              string? colDep = null, Dictionary<string, string>? dt = null, Dictionary<string, string>? us = null)
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
        string joinProv = conProv ? "LEFT JOIN MA_PROVEEDORES P ON C.c_CodProveedor = P.c_CodProveed" : "";
        string joinMon = conMoneda ? "LEFT JOIN MA_MONEDAS M ON M.c_codmoneda = C.c_codmoneda" : "";
        // nombre del proveedor: la DESCRIPCIÓN (c_Descripcio); si está vacía o en "0", la razón social (c_razon)
        string Limpio(string e) => $"NULLIF(NULLIF({e}, N''), N'0')";
        string prov = !conProv ? "N''"
            : $"COALESCE({Limpio(S(pr, "P", "c_Descripcio", "c_Descripcion"))}, {Limpio(S(pr, "P", "c_razon", "c_RazonSocial"))}, N'')";
        string rif = conProv ? S(pr, "P", "c_Rif") : "N''";
        string moneda = conMoneda ? S(mo, "M", "c_descripcion") : S(c, "C", "c_codmoneda");
        string simbolo = conMoneda ? S(mo, "M", "c_simbolo") : "N''";
        // tasa del documento (n_FACTORCAMBIO, la del día de la compra); si no tiene, el factor ACTUAL de la moneda
        string factorMon = conMoneda ? $"NULLIF({N(mo, "M", "n_factor")}, 0)" : "NULL";
        string factor = c.ContainsKey("n_FactorCambio") ? $"COALESCE(NULLIF({N(c, "C", "n_FactorCambio")}, 0), {factorMon}, 1)"
                      : $"COALESCE({factorMon}, 1)";
        string status = c.ContainsKey("c_Status") ? " AND C.c_Status = N'DCO'" : "";
        // descripción del artículo: la de la ficha; si no tiene, la del renglón
        string descri = pd.ContainsKey("c_Descri") && ti.ContainsKey("c_Descripcion")
            ? "COALESCE(NULLIF(LTRIM(RTRIM(Pd.c_Descri)), N''), LTRIM(RTRIM(ISNULL(I.c_Descripcion, N''))))"
            : pd.ContainsKey("c_Descri") ? S(pd, "Pd", "c_Descri") : S(ti, "I", "c_Descripcion");
        string cantidad = N(ti, "I", "n_cant_teorica", "n_Cantidad");
        string empaque = N(ti, "I", "ns_CantidadEmpaque");
        string porPeso = pd.ContainsKey("n_TipoPeso") ? "CASE WHEN Pd.n_TipoPeso = 1 THEN 1 ELSE 0 END" : "0";
        string orden = ti.ContainsKey("c_Linea") ? "I.c_Linea" : "1";
        // departamento: el nombre (MA_DEPARTAMENTOS.c_descripcio) si existe la tabla; si no, el código de la ficha
        string? colDepto = Primera(pd, "c_Departamento", "c_CodDepartamento", "c_Grupo");
        bool conDeptos = colDepto != null && dt != null && dt.ContainsKey("c_codigo") && (dt.ContainsKey("c_descripcio") || dt.ContainsKey("c_descripcion"));
        string codDepto = colDepto == null ? "N''" : $"LTRIM(RTRIM(CONVERT(NVARCHAR(50), Pd.[{colDepto}])))";
        string depto = !conDeptos ? codDepto
            : $"COALESCE(NULLIF((SELECT TOP 1 LTRIM(RTRIM(Dt.[{(dt!.ContainsKey("c_descripcio") ? "c_descripcio" : "c_descripcion")}])) FROM MA_DEPARTAMENTOS Dt WHERE Dt.c_codigo = Pd.[{colDepto}]), N''), {codDepto})";
        // filtro de localidad: @l0, @l1… (los códigos vienen validados contra la lista configurada)
        // localidad del documento: su columna, o la del depósito
        string? exprLoc = colLoc != null ? $"LTRIM(RTRIM(CONVERT(NVARCHAR(50), C.[{colLoc}])))"
                        : colDep != null ? "LTRIM(RTRIM(CONVERT(NVARCHAR(50), Dp.c_CodLocalidad)))" : null;
        string joinDep = colLoc == null && colDep != null ? $"LEFT JOIN MA_DEPOSITO Dp ON Dp.c_CodDeposito = C.[{colDep}]" : "";
        string filtroLoc = exprLoc == null || nLoc == 0 ? ""
            : $" AND {exprLoc} IN ({string.Join(", ", Enumerable.Range(0, nLoc).Select(i => "@l" + i))})";


        return $@"
SET NOCOUNT ON;
DECLARE @desde DATETIME = @p1, @hasta DATETIME = DATEADD(DAY, 1, @p2);
CREATE TABLE #D (Id INT IDENTITY(0, 1) PRIMARY KEY, Doc NVARCHAR(100) COLLATE DATABASE_DEFAULT NOT NULL,
    Concepto NVARCHAR(20) COLLATE DATABASE_DEFAULT NOT NULL, Fecha NVARCHAR(30),
    CodProv NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Proveedor NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Rif NVARCHAR(4000) COLLATE DATABASE_DEFAULT,
    FactProv NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Moneda NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Simbolo NVARCHAR(4000) COLLATE DATABASE_DEFAULT,
    Factor DECIMAL(19,6), Subtotal DECIMAL(19,4), Descuento DECIMAL(19,4), Impuesto DECIMAL(19,4), Total DECIMAL(19,4),
    Localidad NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Usuario NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Obs NVARCHAR(4000) COLLATE DATABASE_DEFAULT,
    NControl NVARCHAR(4000) COLLATE DATABASE_DEFAULT, FechaEmision NVARCHAR(30), Relacion NVARCHAR(4000) COLLATE DATABASE_DEFAULT,
    Ejecutor NVARCHAR(4000) COLLATE DATABASE_DEFAULT, Retencion DECIMAL(19,4), BaseImp DECIMAL(19,4));

INSERT INTO #D (Doc, Concepto, Fecha, CodProv, Proveedor, Rif, FactProv, Moneda, Simbolo, Factor, Subtotal, Descuento, Impuesto, Total, Localidad, Usuario, Obs,
                NControl, FechaEmision, Relacion, Ejecutor, Retencion, BaseImp)
SELECT LEFT(LTRIM(RTRIM(C.c_Documento)), 100), LEFT(UPPER(LTRIM(RTRIM(C.c_Concepto))), 20), CONVERT(CHAR(19), C.[{colFecha}], 120), {S(c, "C", "c_CodProveedor")}, {prov}, {rif},
       {S(c, "C", "FACPROVEEDOR", "c_FacProveedor", "c_Factura", "c_NumFactura", "c_NroFactura", "c_Nro_Factura", "c_Factura_Prov", "c_FacturaProveedor", "c_DocProveedor",
            "c_DocumentoProveedor", "c_Documento_Proveedor", "c_Num_Factura", "c_NumDocProv", "c_FactProveedor")},
       {moneda}, {simbolo}, CAST({factor} AS DECIMAL(19,6)),
       {R(N(c, "C", "n_Subtotal", "n_SubTotal", "n_Monto"))}, {R(N(c, "C", "n_Descuento", "n_MontoDescuento"))},
       {R(N(c, "C", "n_Impuesto", "n_Iva", "n_MontoImpuesto"))}, {R(N(c, "C", "n_Total", "n_MontoTotal"))},
       {exprLoc ?? "N''"}, {ConsultaOdc.Usuario(S(c, "C", "c_CodComprador", "c_Comprador", "c_Usuario", "c_CodUsuario", "cu_Usuario"), us)},
       LEFT(REPLACE(REPLACE({S(c, "C", "c_Observacion", "c_Descripcion", "c_Comentario").Replace("NVARCHAR(200)", "NVARCHAR(4000)")}, CHAR(13), N''), CHAR(10), N' · '), 4000),
       {S(c, "C", "cs_ncontrol", "c_NumControl", "c_NControl")},
       {(c.ContainsKey("ds_fecha_emision") ? "CONVERT(CHAR(19), C.ds_fecha_emision, 120)" : "N''")},
       {S(c, "C", "c_RELACION", "c_Relacion")}, {ConsultaOdc.Usuario(S(c, "C", "c_Ejecutor"), us)},
       {R(N(c, "C", "NS_RETENCION", "n_Retencion"))}, {R(N(c, "C", "n_baseimp", "n_BaseImponible"))}
FROM MA_COMPRAS C
{joinProv}
{joinMon}
{joinDep}
WHERE C.c_Concepto IN (N'COM', N'DCM'){status}{filtroLoc} AND C.[{colFecha}] >= @desde AND C.[{colFecha}] < @hasta
ORDER BY C.[{colFecha}], C.c_Concepto, C.c_Documento;

CREATE INDEX IX_D ON #D (Doc, Concepto);
SELECT Doc, Fecha, CodProv, Proveedor, Rif, FactProv, Moneda, Simbolo, Factor, Subtotal, Descuento, Impuesto, Total, Localidad, Usuario, Obs, Concepto,
       NControl, FechaEmision, Relacion, Ejecutor, Retencion, BaseImp
FROM #D ORDER BY Id;

SELECT D.Id, {(ti.ContainsKey("c_Linea") ? "LTRIM(RTRIM(CONVERT(NVARCHAR(20), I.c_Linea)))" : "N''")}, LTRIM(RTRIM(I.c_CodArticulo)), {descri},
       {R(cantidad)},
       CASE WHEN {porPeso} = 0 AND {empaque} > 0 THEN CAST({cantidad} * 1.0 / {empaque} AS INT) ELSE 0 END,
       {R($"CASE WHEN {porPeso} = 1 THEN {cantidad} ELSE 0 END")}, {R(empaque)},
       {R(N(ti, "I", "n_Costo"))}, {R(N(ti, "I", "n_Precio"))}, {R(N(ti, "I", "n_Precio_Original"))},
       {R(N(ti, "I", "n_DescuentoEspecifico"))}, {R(N(ti, "I", "n_DescuentoGeneral"))},
       {R(N(ti, "I", "n_Subtotal"))}, {R(N(ti, "I", "n_Impuesto"))}, {R(N(ti, "I", "n_Total"))}, {R(N(ti, "I", "IMPUESTO", "n_PorcImpuesto"))},
       {R(N(pd, "Pd", "n_Costorep"))}, {R(N(pd, "Pd", "n_Precio1"))}, {S(pd, "Pd", "c_Presenta")}, {porPeso},
       {depto}, {(pd.ContainsKey("c_Codigo") ? "CASE WHEN Pd.c_Codigo IS NULL THEN 0 ELSE 1 END" : "1")}
FROM #D D
JOIN TR_INVENTARIO I ON I.c_Documento = D.Doc AND I.c_Concepto = D.Concepto
LEFT JOIN MA_PRODUCTOS Pd ON Pd.c_Codigo = I.c_CodArticulo
ORDER BY D.Id, {orden};
DROP TABLE #D;";
    }

    public static async Task<ResultadoCompras> EjecutarAsync(string cadena, DateTime desde, DateTime hasta, IReadOnlyList<string> localidades,
                                                             int timeoutSeg, CancellationToken ct, string origenOdc = "00")
    {
        await using var conn = new SqlConnection(cadena);
        await conn.OpenAsync(ct);
        var c = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_COMPRAS", timeoutSeg, ct);
        var ti = await ConsultaCierres.ColumnasAsync(conn, cadena, "TR_INVENTARIO", timeoutSeg, ct);
        if (c.Count == 0 || ti.Count == 0) throw new InvalidOperationException("La base no tiene las tablas MA_COMPRAS / TR_INVENTARIO (¿es la base central VAD10?).");
        foreach (var (t, cols, col) in new[] { ("MA_COMPRAS", c, "c_Documento"), ("MA_COMPRAS", c, "c_Concepto"), ("TR_INVENTARIO", ti, "c_Documento"),
                                               ("TR_INVENTARIO", ti, "c_Concepto"), ("TR_INVENTARIO", ti, "c_CodArticulo") })
            if (!cols.ContainsKey(col)) throw new InvalidOperationException($"La tabla {t} no tiene la columna {col}.");
        var pr = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_PROVEEDORES", timeoutSeg, ct);
        var mo = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_MONEDAS", timeoutSeg, ct);
        var pd = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_PRODUCTOS", timeoutSeg, ct);
        var dt = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_DEPARTAMENTOS", timeoutSeg, ct);
        var us = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_USUARIOS", timeoutSeg, ct);
        string? fecha = Primera(c, FechasPosibles);
        if (fecha == null)
        {
            var fechas = c.Where(x => x.Value.Contains("date")).Select(x => x.Key).ToList();
            throw new InvalidOperationException("No se encontró la columna de fecha de MA_COMPRAS. Columnas de fecha que tiene: "
                                                + (fechas.Count > 0 ? string.Join(", ", fechas) : "(ninguna)"));
        }

        string? loc = Primera(c, LocalidadPosibles);
        string? dep = null;
        if (loc == null && c.ContainsKey("c_CodDeposito"))
        {
            var dp = await ConsultaCierres.ColumnasAsync(conn, cadena, "MA_DEPOSITO", timeoutSeg, ct);
            if (dp.ContainsKey("c_CodDeposito") && dp.ContainsKey("c_CodLocalidad")) dep = "c_CodDeposito";
        }
        if (loc == null && dep == null && localidades.Count > 0)
            throw new InvalidOperationException("MA_COMPRAS no tiene la columna de localidad (c_CodLocalidad) ni de depósito: no se puede filtrar por localidad.");

        var docs = new List<object?[]>();
        var lineas = new List<object?[]>();
        await using (var cmd = new SqlCommand(Lectura.Prefijo + Sql(c, pr, mo, ti, pd, fecha, loc, localidades.Count, dep, dt, us), conn) { CommandTimeout = timeoutSeg })
        {
            cmd.Parameters.AddWithValue("@p1", desde.Date);
            cmd.Parameters.AddWithValue("@p2", hasta.Date);
            for (int i = 0; i < localidades.Count; i++) cmd.Parameters.AddWithValue("@l" + i, localidades[i]);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await Leer(rd, docs, ct);
            if (await rd.NextResultAsync(ct)) await Leer(rd, lineas, ct);
        }
        // órdenes de compra del mismo período y localidades (para "pedido vs recibido")
        var odc = await ConsultaOdc.EjecutarAsync(conn, cadena, desde, hasta, localidades, origenOdc, timeoutSeg, ct);
        return new ResultadoCompras(docs, lineas, fecha, odc.Ordenes, odc.Lineas, odc.Aviso, odc.Recepciones);
    }

    internal static async Task Leer(SqlDataReader rd, List<object?[]> destino, CancellationToken ct)
    {
        int n = rd.FieldCount;
        var tipos = Enumerable.Range(0, n).Select(rd.GetFieldType).ToArray();
        while (await rd.ReadAsync(ct))
        {
            var f = new object?[n];
            for (int i = 0; i < n; i++)
            {
                var t = tipos[i];
                bool numero = t == typeof(decimal) || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(double) || t == typeof(float);
                if (rd.IsDBNull(i)) f[i] = numero ? 0 : "";
                else if (t == typeof(int) || t == typeof(long) || t == typeof(short)) f[i] = Convert.ToInt64(rd.GetValue(i));
                else if (numero) f[i] = Convert.ToDecimal(rd.GetValue(i));
                else f[i] = Convert.ToString(rd.GetValue(i))!.Trim();
            }
            destino.Add(f);
        }
    }
}
