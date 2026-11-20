using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>Filas del Libro de ventas I.V.A. de una tienda (ya agrupadas) y avisos sobre los datos que faltan en su KLK.</summary>
public sealed record ResultadoLibroIva(List<object?[]> Filas, List<string> Avisos);

/// <summary>
/// "Libro de ventas I.V.A." (copia del reporte fiscal del KLK), una tienda:
///   - Facturas fiscales (KLK_FACTURAHDR: NoFiscal = 0, EnEspera = 0, con NumTicketFiscal): UNA FILA POR REPORTE Z
///     (serial de la impresora + KLK_FACTURAHDR.NCorteZ), con la primera y la última factura del Z.
///   - Notas de crédito fiscales (KLK_NCREDITOHDR): una fila por nota.
///   - Serial de la impresora: KLK_CIERRE de esa caja y apertura si existe; si no, KLK_CAJA.SerialImpresora.
///   - Base imponible del documento = Total − IVA − IGTF − Exento (lo gravado, ya con los descuentos); la base y el IVA se reparten
///     entre 16 %, 8 % y 31 % según los renglones (PorcImpuesto); un documento sin renglones gravados va todo al 16 %.
/// Montos positivos; el complemento pone las notas de crédito en negativo.
/// </summary>
public static class ConsultaLibroIva
{
    // Fila: 0 Tipo (FACT / NC), 1 Fecha "yyyy-MM-dd", 2 Serial impresora, 3 Nro. Z, 4 Inicial, 5 Final, 6 Exento, 7 Base 16 %,
    //       8 IVA 16 %, 9 Base 8 %, 10 IVA 8 %, 11 Base 31 %, 12 IVA 31 %, 13 IVA retenido, 14 Documentos, 15 Factura afectada (notas),
    //       16 Orden de emisión (las notas de una misma impresora y día salen en el orden en que se hicieron, como en el KLK),
    //       17 Exento USD, 18 Base imponible USD (todas las alícuotas), 19 IVA USD, 20 Documentos sin tasa
    //       (USD = monto en Bs ÷ tasa del propio documento: TasaUSD y si no, Tasa; en la nota, la de la nota y si no, la de la factura)
    private static readonly string[] Tablas = { "KLK_FACTURAHDR", "KLK_FACTURALINE", "KLK_NCREDITOHDR", "KLK_NCREDITOLINE", "KLK_CAJA", "KLK_CIERRE" };

    private static string Sql(Dictionary<string, Dictionary<string, string>> c, List<string> avisos)
    {
        var fh = c["KLK_FACTURAHDR"]; var fl = c["KLK_FACTURALINE"]; var nh = c["KLK_NCREDITOHDR"]; var nl = c["KLK_NCREDITOLINE"];
        var cj = c["KLK_CAJA"]; var ci = c["KLK_CIERRE"];
        static string N(Dictionary<string, string> t, string a, string col) =>
            !t.TryGetValue(col, out var ty) ? "0"
            : ConsultaCierres.EsNumero(ty) ? $"ISNULL({a}.[{col}], 0)"
            : $"ISNULL(TRY_CONVERT(DECIMAL(19,6), {a}.[{col}]), 0)";
        static string S(Dictionary<string, string> t, string a, string col, int largo = 50) =>
            t.ContainsKey(col) ? $"LTRIM(RTRIM(ISNULL(CONVERT(NVARCHAR({largo}), {a}.[{col}]), N'')))" : "N''";
        static string Si(Dictionary<string, string> t, string a, string col) =>
            t.ContainsKey(col) ? $" AND ISNULL(CONVERT(NVARCHAR(10), {a}.[{col}]), N'0') NOT IN (N'1', N'True', N'S')" : "";

        // serial de la impresora fiscal: el del cierre de esa caja y apertura (histórico) y si no, el de la caja (actual)
        bool conCaja = cj.ContainsKey("CodCaja") && cj.ContainsKey("SerialImpresora");
        bool conCierre = ci.ContainsKey("CodCaja") && ci.ContainsKey("CodApertura") && ci.ContainsKey("SerialImpresora");
        if (!conCaja && !conCierre) avisos.Add("La base no tiene KLK_CAJA.SerialImpresora: en «Impresora Fiscal» sale el código de la caja.");
        // un serial "0" o vacío no sirve (algunas cajas lo guardan así): se pasa al siguiente y, si ninguno sirve, «CAJA 000010»
        string Serial(string a) =>
            "COALESCE(" +
            (conCierre ? $"(SELECT TOP 1 LTRIM(RTRIM(CI.SerialImpresora)) FROM KLK_CIERRE CI WHERE CI.CodCaja = {a}.CodCaja AND CI.CodApertura = {a}.CodApertura AND LTRIM(RTRIM(ISNULL(CI.SerialImpresora, N''))) NOT IN (N'', N'0')), " : "") +
            (conCaja ? $"(SELECT TOP 1 LTRIM(RTRIM(CJ.SerialImpresora)) FROM KLK_CAJA CJ WHERE CJ.CodCaja = {a}.CodCaja AND LTRIM(RTRIM(ISNULL(CJ.SerialImpresora, N''))) NOT IN (N'', N'0')), " : "") +
            $"N'CAJA ' + LTRIM(RTRIM({a}.CodCaja)))";
        if (!fh.ContainsKey("NCorteZ")) avisos.Add("KLK_FACTURAHDR no tiene NCorteZ: las facturas se agrupan por impresora y día.");

        // tasa del documento (Bs por USD): la primera columna con valor
        string Tasa(Dictionary<string, string> t, string a, params string[] cols)
        {
            var hay = cols.Where(t.ContainsKey).Select(c => $"NULLIF({N(t, a, c)}, 0)").ToList();
            return hay.Count == 0 ? "0" : $"COALESCE({string.Join(", ", hay)}, 0)";
        }
        string baseDoc(Dictionary<string, string> t, string a) =>
            $"{N(t, a, "Total")} - {N(t, a, "MontoImpuesto")} - {N(t, a, "MontoIGTF")} - {N(t, a, "Exento")}";
        string ordenNl = nl.ContainsKey("Orden") ? ConsultaCierres.Igual("NL.Orden", ConsultaCierres.Tipo(nl, "Orden"), "D.Ap", "nvarchar") : "1 = 1";
        // reparto por alícuota según los renglones (PorcImpuesto con un margen por redondeo)
        string Pesos(Dictionary<string, string> t, string a) =>
            !t.ContainsKey("PorcImpuesto") ? "0, 0, 0, 0, 0, 0" :
            string.Join(", ", new[] { (15.5m, 16.5m), (7.5m, 8.5m), (30.5m, 31.5m) }.Select(r =>
                $"SUM(CASE WHEN {a}.PorcImpuesto BETWEEN {r.Item1} AND {r.Item2} THEN {N(t, a, "TotalLinea")} ELSE 0 END)")
            .Concat(new[] { (15.5m, 16.5m), (7.5m, 8.5m), (30.5m, 31.5m) }.Select(r =>
                $"SUM(CASE WHEN {a}.PorcImpuesto BETWEEN {r.Item1} AND {r.Item2} THEN {N(t, a, "MontoImpuesto")} ELSE 0 END)")));

        return $@"
SET NOCOUNT ON;
DECLARE @desde DATETIME = @p1, @hasta DATETIME = DATEADD(DAY, 1, @p2);
CREATE TABLE #X (Id INT IDENTITY(0, 1) PRIMARY KEY, Tipo NCHAR(4) NOT NULL, Num NVARCHAR(50) COLLATE DATABASE_DEFAULT NOT NULL,
    Caja NVARCHAR(50) COLLATE DATABASE_DEFAULT NOT NULL, Ap NVARCHAR(50) COLLATE DATABASE_DEFAULT NOT NULL, Fecha DATE NOT NULL,
    Serial NVARCHAR(50) COLLATE DATABASE_DEFAULT, Z NVARCHAR(50) COLLATE DATABASE_DEFAULT, Ticket NVARCHAR(50) COLLATE DATABASE_DEFAULT,
    Exento DECIMAL(19,6), Iva DECIMAL(19,6), Base DECIMAL(19,6), Ret DECIMAL(19,6), Afectada NVARCHAR(50) COLLATE DATABASE_DEFAULT,
    Tasa DECIMAL(19,6));

INSERT INTO #X (Tipo, Num, Caja, Ap, Fecha, Serial, Z, Ticket, Exento, Iva, Base, Ret, Afectada, Tasa)
SELECT N'FACT', FH.NumFactura, FH.CodCaja, FH.CodApertura, CAST(FH.FechaFactura AS DATE), {Serial("FH")}, {S(fh, "FH", "NCorteZ")},
       {S(fh, "FH", "NumTicketFiscal")}, {N(fh, "FH", "Exento")}, {N(fh, "FH", "MontoImpuesto")}, {baseDoc(fh, "FH")}, {N(fh, "FH", "Retencion")}, N'', {Tasa(fh, "FH", "TasaUSD", "Tasa")}
FROM KLK_FACTURAHDR FH
WHERE FH.FechaFactura >= @desde AND FH.FechaFactura < @hasta AND FH.EnEspera = 0{Si(fh, "FH", "NoFiscal")}
  AND ISNULL(FH.NumTicketFiscal, N'') <> N'';

INSERT INTO #X (Tipo, Num, Caja, Ap, Fecha, Serial, Z, Ticket, Exento, Iva, Base, Ret, Afectada, Tasa)
SELECT N'NC', NH.NumNota, NH.CodCaja, NH.CodApertura, CAST(NH.FechaNota AS DATE), {Serial("NH")}, {S(nh, "NH", "NCorteZ")},
       {S(nh, "NH", "NumTicketFiscal")}, {N(nh, "NH", "Exento")}, {N(nh, "NH", "MontoImpuesto")}, {baseDoc(nh, "NH")}, {N(nh, "NH", "Retencion")},
       {(nh.ContainsKey("NumFactFiscalAfectada") ? $"COALESCE(NULLIF({S(nh, "NH", "NumFactFiscalAfectada")}, N''), {S(nh, "NH", "FacturaAfectada")})" : S(nh, "NH", "FacturaAfectada"))},
       {Tasa(nh, "NH", "TasaUSD", "TasaUSDFact", "Tasa")}
FROM KLK_NCREDITOHDR NH
WHERE NH.FechaNota >= @desde AND NH.FechaNota < @hasta{Si(nh, "NH", "NoFiscal")}
  AND ISNULL(NH.NumTicketFiscal, N'') <> N''
ORDER BY NH.FechaNota{(nh.ContainsKey("Hora") ? ", NH.Hora" : "")}, NH.NumNota;

CREATE INDEX IX_X ON #X (Tipo, Num, Caja, Ap);

-- pesos por alícuota de cada documento (base y IVA de sus renglones)
CREATE TABLE #W (Id INT PRIMARY KEY, B16 DECIMAL(19,6), B8 DECIMAL(19,6), B31 DECIMAL(19,6), I16 DECIMAL(19,6), I8 DECIMAL(19,6), I31 DECIMAL(19,6));
INSERT INTO #W
SELECT D.Id, {Pesos(fl, "FL")}
FROM #X D JOIN KLK_FACTURALINE FL ON FL.NumFactura = D.Num AND FL.CodCaja = D.Caja AND FL.CodApertura = D.Ap
WHERE D.Tipo = N'FACT' GROUP BY D.Id;
INSERT INTO #W
SELECT D.Id, {Pesos(nl, "NL")}
FROM #X D JOIN KLK_NCREDITOLINE NL ON NL.NumNota = D.Num AND NL.CodCaja = D.Caja AND {ordenNl}
WHERE D.Tipo = N'NC' GROUP BY D.Id;

-- cada documento con su base e IVA repartidos: 8 % y 31 % por su peso, el resto al 16 %
SELECT D.Id, D.Tipo, D.Fecha, D.Caja, D.Serial, D.Z, D.Ticket, D.Exento, D.Ret, D.Afectada, D.Tasa,
       B8  = CASE WHEN W.B16 + W.B8 + W.B31 <> 0 THEN D.Base * W.B8  / (W.B16 + W.B8 + W.B31) ELSE 0 END,
       B31 = CASE WHEN W.B16 + W.B8 + W.B31 <> 0 THEN D.Base * W.B31 / (W.B16 + W.B8 + W.B31) ELSE 0 END,
       I8  = CASE WHEN W.I16 + W.I8 + W.I31 <> 0 THEN D.Iva * W.I8  / (W.I16 + W.I8 + W.I31) ELSE 0 END,
       I31 = CASE WHEN W.I16 + W.I8 + W.I31 <> 0 THEN D.Iva * W.I31 / (W.I16 + W.I8 + W.I31) ELSE 0 END,
       D.Base, D.Iva
INTO #R
FROM #X D LEFT JOIN #W W ON W.Id = D.Id;

-- facturas: una fila por reporte Z (impresora + Z; sin Z: por impresora y día)
SELECT Tipo = N'FACT', Fecha = CONVERT(CHAR(10), MAX(Fecha), 120), Serial, Z,
       ISNULL(MIN(CASE WHEN TRY_CONVERT(BIGINT, Ticket) > 0 THEN Ticket END), MIN(Ticket)),
       ISNULL(MAX(CASE WHEN TRY_CONVERT(BIGINT, Ticket) > 0 THEN Ticket END), MAX(Ticket)),
       CAST(SUM(Exento) AS DECIMAL(19,6)), CAST(SUM(Base - B8 - B31) AS DECIMAL(19,6)), CAST(SUM(Iva - I8 - I31) AS DECIMAL(19,6)),
       CAST(SUM(B8) AS DECIMAL(19,6)), CAST(SUM(I8) AS DECIMAL(19,6)), CAST(SUM(B31) AS DECIMAL(19,6)), CAST(SUM(I31) AS DECIMAL(19,6)),
       CAST(SUM(Ret) AS DECIMAL(19,6)), COUNT(*), N'', MIN(Id),
       CAST(SUM(CASE WHEN Tasa > 0 THEN Exento / Tasa ELSE 0 END) AS DECIMAL(19,6)), CAST(SUM(CASE WHEN Tasa > 0 THEN Base / Tasa ELSE 0 END) AS DECIMAL(19,6)),
       CAST(SUM(CASE WHEN Tasa > 0 THEN Iva / Tasa ELSE 0 END) AS DECIMAL(19,6)), SUM(CASE WHEN Tasa > 0 THEN 0 ELSE 1 END)
FROM #R WHERE Tipo = N'FACT'
GROUP BY Caja, Serial, Z, CASE WHEN Z = N'' THEN Fecha END     -- cada caja es una impresora: nunca se mezclan dos cajas en una fila
UNION ALL
-- notas de crédito: una por una
SELECT N'NC', CONVERT(CHAR(10), Fecha, 120), Serial, Z, Ticket, Ticket,
       CAST(Exento AS DECIMAL(19,6)), CAST(Base - B8 - B31 AS DECIMAL(19,6)), CAST(Iva - I8 - I31 AS DECIMAL(19,6)),
       CAST(B8 AS DECIMAL(19,6)), CAST(I8 AS DECIMAL(19,6)), CAST(B31 AS DECIMAL(19,6)), CAST(I31 AS DECIMAL(19,6)),
       CAST(Ret AS DECIMAL(19,6)), 1, Afectada, Id,
       CAST(CASE WHEN Tasa > 0 THEN Exento / Tasa ELSE 0 END AS DECIMAL(19,6)), CAST(CASE WHEN Tasa > 0 THEN Base / Tasa ELSE 0 END AS DECIMAL(19,6)),
       CAST(CASE WHEN Tasa > 0 THEN Iva / Tasa ELSE 0 END AS DECIMAL(19,6)), CASE WHEN Tasa > 0 THEN 0 ELSE 1 END
FROM #R WHERE Tipo = N'NC'
ORDER BY 2, 3, 1, 17;
DROP TABLE #R; DROP TABLE #W; DROP TABLE #X;";
    }

    public static async Task<ResultadoLibroIva> EjecutarAsync(string cadena, DateTime desde, DateTime hasta, int timeoutSeg, CancellationToken ct)
    {
        await using var conn = new SqlConnection(cadena);
        await conn.OpenAsync(ct);
        var c = new Dictionary<string, Dictionary<string, string>>();
        foreach (var t in Tablas) c[t] = await ConsultaCierres.ColumnasAsync(conn, cadena, t, timeoutSeg, ct);
        foreach (var (t, col) in new[] { ("KLK_FACTURAHDR", "FechaFactura"), ("KLK_FACTURAHDR", "EnEspera"), ("KLK_FACTURAHDR", "NumTicketFiscal"),
                                         ("KLK_NCREDITOHDR", "FechaNota"), ("KLK_NCREDITOHDR", "NumTicketFiscal"), ("KLK_FACTURALINE", "NumFactura"),
                                         ("KLK_NCREDITOLINE", "NumNota") })
            if (!c[t].ContainsKey(col)) throw new InvalidOperationException($"La tabla {t} no tiene la columna {col}.");
        var avisos = new List<string>();
        var filas = new List<object?[]>();
        await using var cmd = new SqlCommand(Lectura.Prefijo + Sql(c, avisos), conn) { CommandTimeout = timeoutSeg };
        cmd.Parameters.AddWithValue("@p1", desde.Date);
        cmd.Parameters.AddWithValue("@p2", hasta.Date);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await ConsultaCompras.Leer(rd, filas, ct);
        return new ResultadoLibroIva(filas, avisos);
    }
}
