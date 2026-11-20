using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>Resultado del "Listado de ventas": documentos, sus renglones y sus pagos (los renglones y pagos apuntan al documento por índice).</summary>
public sealed record ResultadoListado(List<object?[]> Documentos, List<object?[]> Lineas, List<object?[]> Pagos, bool PagosDisponibles);

/// <summary>
/// "Listado de ventas" (equivale al "Listado de ventas general detallado" del KLK, pero en columnas):
///   1. Documentos: facturas (KLK_FACTURAHDR, EnEspera = 0) y notas de crédito (KLK_NCREDITOHDR) del período.
///   2. Renglones:  KLK_FACTURALINE / KLK_NCREDITOLINE (la línea de la nota se enlaza por NL.Orden = NH.CodApertura).
///   3. Pagos:      KLK_COBROLINE del cobro de cada factura (FH.NCobro = CL.NroCobro, misma caja y apertura);
///                  si la factura no tiene NCobro se busca por CL.NFactura.
/// Montos tal como están en la base (positivos); el complemento pone en negativo las devoluciones.
/// El total USD de cada renglón usa la misma fórmula que el resumen de ventas, así los totales cuadran entre reportes.
/// Las columnas que no existan en la versión del KLK de una tienda salen en 0 / vacías.
/// </summary>
public static class ConsultaListado
{
    // Documento: 0 Tipo (VEN/DEV), 1 Número, 2 Ticket fiscal, 3 Fecha "yyyy-MM-dd HH:mm:ss", 4 Caja, 5 Nombre caja,
    //            6 Cód. cajero, 7 Cajero, 8 RIF, 9 Cliente, 10 SubTotal, 11 Descuento, 12 Impuesto, 13 Exento, 14 IGTF,
    //            15 Total, 16 Tasa, 17 Anulado (0/1), 18 Nro. cobro (facturas) o factura afectada (notas), 19 No fiscal (0/1), 20 A crédito (0/1)
    // Renglón:   0 Índice del documento, 1 Línea, 2 Código, 3 Código de barras, 4 Descripción, 5 Cantidad, 6 Precio,
    //            7 Descuento, 8 Total línea (antes de descuento e IVA), 9 IVA, 10 % IVA, 11 Total USD (con IVA, sin descuentos)
    // Pago:      0 Índice del documento, 1 Nro. cobro, 2 Línea, 3 Fecha, 4 Forma de pago, 5 Cód. forma, 6 Tipo de cobro,
    //            7 Monto, 8 Monto USD, 9 Tasa, 10 Vuelto, 11 Referencia, 12 Banco, 13 Referencia USD, 14 Banco USD
    private static string Sql(Dictionary<string, string> fh, Dictionary<string, string> fl, Dictionary<string, string> nh,
                              Dictionary<string, string> nl, Dictionary<string, string> cl, Dictionary<string, string> usu)
    {
        static string N(Dictionary<string, string> t, string a, string col) =>
            !t.TryGetValue(col, out var ty) ? "0"
            : ConsultaCierres.EsNumero(ty) ? $"ISNULL({a}.[{col}], 0)"
            : $"ISNULL(TRY_CONVERT(DECIMAL(19,6), {a}.[{col}]), 0)";
        static string S(Dictionary<string, string> t, string a, string col, int largo = 100) =>
            t.ContainsKey(col) ? $"LTRIM(RTRIM(ISNULL(CONVERT(NVARCHAR({largo}), {a}.[{col}]), N'')))" : "N''";
        static string B(Dictionary<string, string> t, string a, string col) =>
            t.ContainsKey(col) ? $"CASE WHEN CONVERT(NVARCHAR(10), {a}.[{col}]) IN (N'1', N'S', N'True') THEN 1 ELSE 0 END" : "0";
        static string R(string expr) => $"CAST({expr} AS DECIMAL(19,4))";
        // fecha con hora: si la fecha viene sin hora y existe la columna Hora, se combinan
        static string Fecha(Dictionary<string, string> t, string a, string col) =>
            t.ContainsKey("Hora")
                ? $"CASE WHEN {a}.Hora IS NOT NULL AND CAST({a}.[{col}] AS TIME) = '00:00:00' THEN CONVERT(CHAR(10), {a}.[{col}], 120) + ' ' + CONVERT(CHAR(8), {a}.Hora, 108) ELSE CONVERT(CHAR(19), {a}.[{col}], 120) END"
                : $"CONVERT(CHAR(19), {a}.[{col}], 120)";
        static string Tasa(Dictionary<string, string> t, string a) =>
            t.ContainsKey("TasaUSD") && t.ContainsKey("Tasa") ? $"COALESCE(NULLIF({a}.TasaUSD, 0), NULLIF({a}.Tasa, 0), 0)"
            : t.ContainsKey("TasaUSD") ? $"ISNULL({a}.TasaUSD, 0)" : t.ContainsKey("Tasa") ? $"ISNULL({a}.Tasa, 0)" : "0";

        bool conUsuarios = usu.ContainsKey("Id_Usuario") && usu.ContainsKey("Nombre");
        string Cajero(Dictionary<string, string> t, string a) =>
            conUsuarios && t.ContainsKey("CodigoVendedorPOS")
                ? $"COALESCE(NULLIF({S(t, a, "NombreVendedorPOS")}, N''), (SELECT TOP 1 LTRIM(RTRIM(U.Nombre)) FROM KLK_USUARIOS U WHERE {ConsultaCierres.Igual("U.Id_Usuario", ConsultaCierres.Tipo(usu, "Id_Usuario"), a + ".CodigoVendedorPOS", ConsultaCierres.Tipo(t, "CodigoVendedorPOS"))}), N'')"
                : S(t, a, "NombreVendedorPOS");
        string Desc(Dictionary<string, string> t, string a) => $"{N(t, a, "MontoDescuento")} + {N(t, a, "MontoDescuentoFidel")} + {N(t, a, "MontoDescuentoFormPago")}";
        string Usd(Dictionary<string, string> t, string a) =>
            $"{N(t, a, "TotalLineaUSD")} + {N(t, a, "IvaUSD")} - ({N(t, a, "DescuentoUSD")} + {N(t, a, "MontoDescuentoFidelUsd")} + {N(t, a, "MontoDescuentoFormPagoUsd")})";
        string ordenF = fh.ContainsKey("Hora") ? "FH.FechaFactura, FH.Hora, FH.CodCaja, FH.NumFactura" : "FH.FechaFactura, FH.CodCaja, FH.NumFactura";
        string ordenN = nh.ContainsKey("Hora") ? "NH.FechaNota, NH.Hora, NH.CodCaja, NH.NumNota" : "NH.FechaNota, NH.CodCaja, NH.NumNota";
        string ordenNl = nl.ContainsKey("Orden") ? ConsultaCierres.Igual("NL.Orden", ConsultaCierres.Tipo(nl, "Orden"), "D.Ap", "nvarchar") : "1 = 1";
        string anuladoCl = cl.ContainsKey("Anulado") ? " AND ISNULL(CONVERT(NVARCHAR(10), CL.Anulado), N'') NOT IN (N'S', N'1', N'T', N'True')" : "";
        bool hayPagos = cl.ContainsKey("NroCobro") && cl.ContainsKey("CodCaja") && cl.ContainsKey("CodApertura") && cl.ContainsKey("Monto");
        string colsPago = $@"D.Id, {S(cl, "CL", "NroCobro", 50)}, CAST({N(cl, "CL", "NroLinea")} AS INT), {(cl.ContainsKey("Fecha") ? "CONVERT(CHAR(19), CL.Fecha, 120)" : "N''")},
       {S(cl, "CL", "FormadePago")}, {S(cl, "CL", "CodFPago", 15)}, {S(cl, "CL", "TipoCobro", 50)},
       {R(N(cl, "CL", "Monto"))}, {R(N(cl, "CL", "MontoUsd"))}, {R(N(cl, "CL", "Tasa"))}, {R(N(cl, "CL", "MontoCambio"))},
       {S(cl, "CL", "NTransaccion", 50)}, {S(cl, "CL", "NombreBanco")}, {S(cl, "CL", "NTransaccionUSD", 50)}, {S(cl, "CL", "BancoTransferenciaUSD", 50)}";
        string fechaCl = cl.ContainsKey("Fecha") ? " AND CL.Fecha >= DATEADD(DAY, -1, @desde) AND CL.Fecha < DATEADD(DAY, 1, @hasta)" : "";
        string pagos = !hayPagos ? "SELECT TOP 0 1 AS Id;" : $@"
CREATE TABLE #P (Id INT, NroCobro NVARCHAR(50) COLLATE DATABASE_DEFAULT, Linea INT, Fecha NVARCHAR(19), Forma NVARCHAR(100) COLLATE DATABASE_DEFAULT,
                 CodF NVARCHAR(15) COLLATE DATABASE_DEFAULT, TipoC NVARCHAR(50) COLLATE DATABASE_DEFAULT, Monto DECIMAL(19,4), MontoUsd DECIMAL(19,4),
                 Tasa DECIMAL(19,4), Vuelto DECIMAL(19,4), Ref NVARCHAR(50) COLLATE DATABASE_DEFAULT, Banco NVARCHAR(100) COLLATE DATABASE_DEFAULT,
                 RefUsd NVARCHAR(50) COLLATE DATABASE_DEFAULT, BancoUsd NVARCHAR(50) COLLATE DATABASE_DEFAULT);
INSERT INTO #P
SELECT {colsPago}
FROM #D D
JOIN KLK_COBROLINE CL ON CL.NroCobro = D.Cobro AND CL.CodCaja = D.Caja AND CL.CodApertura = D.Ap
WHERE D.Tipo = N'VEN' AND D.Cobro <> N''{(cl.ContainsKey("NFactura") ? " AND (ISNULL(CL.NFactura, N'') = N'' OR CL.NFactura = D.Num)" : "")}{anuladoCl};
{(cl.ContainsKey("NFactura") ? $@"-- facturas sin NCobro: se buscan por número de factura (solo si hay alguna, para no recorrer la tabla de cobros)
IF EXISTS (SELECT 1 FROM #D WHERE Tipo = N'VEN' AND Cobro = N'')
    INSERT INTO #P
    SELECT {colsPago}
    FROM #D D
    JOIN KLK_COBROLINE CL ON CL.NFactura = D.Num AND CL.CodCaja = D.Caja AND CL.CodApertura = D.Ap
    WHERE D.Tipo = N'VEN' AND D.Cobro = N''{anuladoCl}{fechaCl};" : "")}
SELECT * FROM #P ORDER BY Id, NroCobro, Linea;
DROP TABLE #P;";

        return $@"
SET NOCOUNT ON;
DECLARE @desde DATETIME = @p1, @hasta DATETIME = DATEADD(DAY, 1, @p2);
CREATE TABLE #D (
    Id INT IDENTITY(0, 1) PRIMARY KEY, Tipo NCHAR(3) NOT NULL, Num NVARCHAR(50) COLLATE DATABASE_DEFAULT NOT NULL, Caja NVARCHAR(50) COLLATE DATABASE_DEFAULT NOT NULL, Ap NVARCHAR(50) COLLATE DATABASE_DEFAULT NOT NULL,
    Ticket NVARCHAR(50) COLLATE DATABASE_DEFAULT, Fecha NVARCHAR(19) COLLATE DATABASE_DEFAULT, NomCaja NVARCHAR(100) COLLATE DATABASE_DEFAULT, CodCajero NVARCHAR(100) COLLATE DATABASE_DEFAULT, Cajero NVARCHAR(100) COLLATE DATABASE_DEFAULT, Rif NVARCHAR(100) COLLATE DATABASE_DEFAULT, Cliente NVARCHAR(100) COLLATE DATABASE_DEFAULT,
    SubTotal DECIMAL(19,4), Descuento DECIMAL(19,4), Impuesto DECIMAL(19,4), Exento DECIMAL(19,4), IGTF DECIMAL(19,4), Total DECIMAL(19,4), Tasa DECIMAL(19,4),
    Anulado INT, Cobro NVARCHAR(50) COLLATE DATABASE_DEFAULT, NoFiscal INT, Credito INT);

INSERT INTO #D (Tipo, Num, Caja, Ap, Ticket, Fecha, NomCaja, CodCajero, Cajero, Rif, Cliente, SubTotal, Descuento, Impuesto, Exento, IGTF, Total, Tasa, Anulado, Cobro, NoFiscal, Credito)
SELECT N'VEN', FH.NumFactura, FH.CodCaja, FH.CodApertura, {S(fh, "FH", "NumTicketFiscal", 50)}, {Fecha(fh, "FH", "FechaFactura")},
       {S(fh, "FH", "NomCaja")}, {S(fh, "FH", "CodigoVendedorPOS")}, {Cajero(fh, "FH")}, {S(fh, "FH", "Rif")}, {S(fh, "FH", "NomCliente")},
       {R(N(fh, "FH", "SubTotal"))}, {R(N(fh, "FH", "MontoDescuento"))}, {R(N(fh, "FH", "MontoImpuesto"))}, {R(N(fh, "FH", "Exento"))},
       {R(N(fh, "FH", "MontoIGTF"))}, {R(N(fh, "FH", "Total"))}, {R(Tasa(fh, "FH"))}, {B(fh, "FH", "Anulado")}, {S(fh, "FH", "NCobro", 50)},
       {B(fh, "FH", "NoFiscal")}, {B(fh, "FH", "FacturaACredito")}
FROM KLK_FACTURAHDR FH
WHERE FH.FechaFactura >= @desde AND FH.FechaFactura < @hasta AND FH.EnEspera = 0
ORDER BY {ordenF};

INSERT INTO #D (Tipo, Num, Caja, Ap, Ticket, Fecha, NomCaja, CodCajero, Cajero, Rif, Cliente, SubTotal, Descuento, Impuesto, Exento, IGTF, Total, Tasa, Anulado, Cobro, NoFiscal, Credito)
SELECT N'DEV', NH.NumNota, NH.CodCaja, NH.CodApertura, {S(nh, "NH", "NumTicketFiscal", 50)}, {Fecha(nh, "NH", "FechaNota")},
       {S(nh, "NH", "NomCaja")}, {S(nh, "NH", "CodigoVendedorPOS")}, {Cajero(nh, "NH")}, {S(nh, "NH", "Rif")}, {S(nh, "NH", "NomCliente")},
       {R(N(nh, "NH", "SubTotal"))}, {R(N(nh, "NH", "MontoDescuento"))}, {R(N(nh, "NH", "MontoImpuesto"))}, {R(N(nh, "NH", "Exento"))},
       {R(N(nh, "NH", "MontoIGTF"))}, {R(N(nh, "NH", "Total"))}, {R(Tasa(nh, "NH"))}, 0, {S(nh, "NH", "FacturaAfectada", 50)},
       {B(nh, "NH", "NoFiscal")}, 0
FROM KLK_NCREDITOHDR NH
WHERE NH.FechaNota >= @desde AND NH.FechaNota < @hasta
ORDER BY {ordenN};

CREATE INDEX IX_D ON #D (Tipo, Num, Caja, Ap);

SELECT Tipo, Num, Ticket, Fecha, Caja, NomCaja, CodCajero, Cajero, Rif, Cliente, SubTotal, Descuento, Impuesto, Exento, IGTF, Total, Tasa, Anulado,
       Cobro, NoFiscal, Credito
FROM #D ORDER BY Id;

SELECT D.Id, FL.NumLineas, {S(fl, "FL", "CodArticulo", 50)}, {S(fl, "FL", "CodBarra", 50)}, {S(fl, "FL", "Descripcion")},
       {R(N(fl, "FL", "Cantidad"))}, {R(N(fl, "FL", "Precio"))}, {R(Desc(fl, "FL"))}, {R(N(fl, "FL", "TotalLinea"))},
       {R(N(fl, "FL", "MontoImpuesto"))}, {R(N(fl, "FL", "PorcImpuesto"))}, {R(Usd(fl, "FL"))}
FROM #D D
JOIN KLK_FACTURALINE FL ON FL.NumFactura = D.Num AND FL.CodCaja = D.Caja AND FL.CodApertura = D.Ap
WHERE D.Tipo = N'VEN'
UNION ALL
SELECT D.Id, NL.NumLineas, {S(nl, "NL", "CodArticulo", 50)}, {S(nl, "NL", "CodBarra", 50)}, {S(nl, "NL", "Descripcion")},
       {R(N(nl, "NL", "Cantidad"))}, {R(N(nl, "NL", "Precio"))}, {R(Desc(nl, "NL"))}, {R(N(nl, "NL", "TotalLinea"))},
       {R(N(nl, "NL", "MontoImpuesto"))}, {R(N(nl, "NL", "PorcImpuesto"))}, {R(Usd(nl, "NL"))}
FROM #D D
JOIN KLK_NCREDITOLINE NL ON NL.NumNota = D.Num AND NL.CodCaja = D.Caja AND {ordenNl}
WHERE D.Tipo = N'DEV'
ORDER BY 1, 2;
{pagos}
DROP TABLE #D;";
    }

    private static readonly string[] Tablas = { "KLK_FACTURAHDR", "KLK_FACTURALINE", "KLK_NCREDITOHDR", "KLK_NCREDITOLINE", "KLK_COBROLINE", "KLK_USUARIOS" };

    public static async Task<ResultadoListado> EjecutarAsync(string cadena, DateTime desde, DateTime hasta, int timeoutSeg, CancellationToken ct)
    {
        await using var conn = new SqlConnection(cadena);
        await conn.OpenAsync(ct);
        var c = new Dictionary<string, Dictionary<string, string>>();
        foreach (var t in Tablas) c[t] = await ConsultaCierres.ColumnasAsync(conn, cadena, t, timeoutSeg, ct);
        foreach (var (t, col) in new[] { ("KLK_FACTURAHDR", "FechaFactura"), ("KLK_FACTURAHDR", "EnEspera"), ("KLK_FACTURALINE", "NumLineas"),
                                         ("KLK_NCREDITOHDR", "FechaNota"), ("KLK_NCREDITOLINE", "NumNota") })
            if (!c[t].ContainsKey(col)) throw new InvalidOperationException($"La tabla {t} no tiene la columna {col}.");

        var docs = new List<object?[]>();
        var lineas = new List<object?[]>();
        var pagos = new List<object?[]>();
        await using var cmd = new SqlCommand(Lectura.Prefijo + Sql(c["KLK_FACTURAHDR"], c["KLK_FACTURALINE"], c["KLK_NCREDITOHDR"],
                                                                   c["KLK_NCREDITOLINE"], c["KLK_COBROLINE"], c["KLK_USUARIOS"]), conn)
        { CommandTimeout = timeoutSeg };
        cmd.Parameters.AddWithValue("@p1", desde.Date);
        cmd.Parameters.AddWithValue("@p2", hasta.Date);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await Leer(rd, docs, ct);
        if (await rd.NextResultAsync(ct)) await Leer(rd, lineas, ct);
        bool hayPagos = c["KLK_COBROLINE"].ContainsKey("NroCobro");
        if (await rd.NextResultAsync(ct) && hayPagos) await Leer(rd, pagos, ct);
        return new ResultadoListado(docs, lineas, pagos, hayPagos);
    }

    /// <summary>Texto recortado, números como decimal (o entero) y nulos como vacío / 0.</summary>
    private static async Task Leer(SqlDataReader rd, List<object?[]> destino, CancellationToken ct)
    {
        int n = rd.FieldCount;
        var tipos = Enumerable.Range(0, n).Select(rd.GetFieldType).ToArray();
        while (await rd.ReadAsync(ct))
        {
            var f = new object?[n];
            for (int i = 0; i < n; i++)
            {
                var t = tipos[i];
                bool numero = t == typeof(decimal) || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(double);
                if (rd.IsDBNull(i)) f[i] = numero ? 0 : "";
                else if (t == typeof(int) || t == typeof(long) || t == typeof(short)) f[i] = Convert.ToInt64(rd.GetValue(i));
                else if (numero) f[i] = Convert.ToDecimal(rd.GetValue(i));
                else f[i] = Convert.ToString(rd.GetValue(i))!.Trim();
            }
            destino.Add(f);
        }
    }
}
