using Microsoft.Data.SqlClient;

namespace VentasReportesApi;

/// <summary>Resultado de "Cierres de caja": cierres (con sistema/declarado/diferencia por forma de pago) y arqueos.</summary>
public sealed record ResultadoCierres(string[] Formas, int[] Signos, List<object?[]> Cierres, List<object?[]> Arqueos,
                                      List<object?[]> SinCierre, bool SinCierreDisponible, string Tabla);

/// <summary>
/// "Cierres de caja": lee KLK_CIERRE_X (cierres por cajero, donde está el Total declarado), igual que getVentasPosStore.
/// Cada forma de pago viene triple: X = sistema, X1 = declarado por el cajero, X2 = diferencia.
/// Como KLK_CIERRE_X puede no tener todas las columnas de KLK_CIERRE, antes se leen sus columnas (sys.columns)
/// y las que no existan salen en 0. Los billetes (KLK_CIERRE_DENOMINACION) se enlazan por NroCierre si existe.
/// </summary>
public static class ConsultaCierres
{
    public const string TablaCierres = "KLK_CIERRE_X";

    // (nombre visible, columna base): el orden define las ternas sistema/declarado/diferencia de cada fila
    // Signo: +1 = forma de pago (suma), −1 = vuelto / IGTF (lo que el sistema suma en las formas y no es venta: resta),
    //         0 = informativo (otra moneda o fondo de caja: se muestra aparte, no entra en los totales en Bs).
    // Las primeras dos deben ser Efectivo y Dólares (el complemento las usa para cruzar con los billetes).
    public static readonly (string Nombre, string Col, int Signo)[] Formas =
    {
        ("Efectivo Bs", "Efectivo", 1), ("Dólares", "Dolares", 1), ("Euros", "Euros", 1), ("Pesos", "Pesos", 1),
        ("Débito", "Debito", 1), ("Crédito", "Credito", 1), ("Transferencia", "Transferencia", 1),
        ("Zelle / Transf. USD", "TransferenciaUSD", 1), ("Tarjeta USD", "TarjetaUSD", 1),
        ("Pago Móvil", "TotalPagoMovil", 1), ("BioPago", "TotalBioPago", 1), ("Merchant", "TotalMerchant", 1),
        ("MegaSoft", "TotalMegaSoft", 1), ("Cashea", "TotalCashea", 1), ("Gift Card", "TotalGiftCard", 1),
        ("Wallet", "TotalWallet", 1), ("E-commerce", "TotalPagoEcommerce", 1), ("Otras formas", "TotalOtrasFormasPago", 1),
        ("Vuelto efectivo Bs", "VueltoEfectivo", -1), ("Vuelto otras formas", "VueltoOFP", -1),
        ("Vuelto Pago Móvil", "VueltoPagoMovil", -1), ("Vuelto transferencia", "VueltoTransferencia", -1),
        ("Vuelto transferencia OM", "VueltoTransferenciaOM", -1), ("Vuelto Merchant", "VueltoMerchant", -1),
        ("IGTF", "IGTF", -1),
        ("Vuelto USD", "VueltoEfectivoUSD", 0), ("Vuelto EUR", "VueltoEfectivoEUR", 0), ("Fondo de caja", "FondoDeCaja", 0)
    };

    // columnas de la tabla por tienda (no cambian seguido): se leen una vez cada 6 horas
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Dictionary<string, string> Cols, DateTime Leido)> columnas = new();

    /// <summary>Columnas de la tabla → tipo SQL (int, nvarchar, decimal, bit…).</summary>
    internal static async Task<Dictionary<string, string>> ColumnasAsync(SqlConnection conn, string cadena, string tabla, int timeoutSeg, CancellationToken ct)
    {
        // Clave sin contraseña: usa origen y base de datos en lugar de la cadena completa
        string clave = $"{conn.DataSource}|{conn.Database}|{tabla}".ToUpperInvariant();
        if (columnas.TryGetValue(clave, out var c) && c.Leido > DateTime.UtcNow.AddHours(-6)) return c.Cols;
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand("SELECT name, TYPE_NAME(system_type_id) FROM sys.columns WHERE object_id = OBJECT_ID(@t)", conn) { CommandTimeout = timeoutSeg })
        {
            cmd.Parameters.AddWithValue("@t", "dbo." + tabla);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) set[Convert.ToString(rd.GetValue(0))!] = (Convert.ToString(rd.GetValue(1)) ?? "").ToLowerInvariant();
        }
        if (set.Count > 0) columnas[clave] = (set, DateTime.UtcNow);
        return set;
    }

    internal static bool EsTexto(string tipo) => tipo.Contains("char") || tipo == "text" || tipo == "ntext";
    internal static bool EsNumero(string tipo) => tipo is "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" or "bit";

    /// <summary>
    /// a = b sin errores de conversión: si una es texto y la otra número, el texto se convierte con TRY_CONVERT
    /// (lo que no sea número queda NULL y no coincide) y la columna numérica conserva su índice.
    /// </summary>
    internal static string Igual(string a, string? tipoA, string b, string? tipoB)
    {
        if (tipoA == null || tipoB == null || EsTexto(tipoA) == EsTexto(tipoB)) return $"{a} = {b}";
        return EsTexto(tipoA) ? $"TRY_CONVERT(BIGINT, {a}) = {b}" : $"{a} = TRY_CONVERT(BIGINT, {b})";
    }

    internal static string? Tipo(Dictionary<string, string> cols, string col) => cols.TryGetValue(col, out var t) ? t : null;

    /// <summary>Expresión de C.NroCierre convertida al tipo de D.NroCierre (así el índice de D se puede usar).</summary>
    private static string NroComoDenominacion(string col, string tipoC, string tipoD) =>
        tipoC == tipoD ? $"C.[{col}]"
        : EsTexto(tipoD) ? $"CONVERT(NVARCHAR(60), C.[{col}])"
        : $"TRY_CONVERT({(tipoD is "decimal" or "numeric" ? "DECIMAL(38,0)" : "BIGINT")}, C.[{col}])";

    private static string SqlCierres(Dictionary<string, string> t, Dictionary<string, string> den, Dictionary<string, string> usu,
                                     Dictionary<string, string> fh, Dictionary<string, string> nh, Dictionary<string, string> nl)
    {
        // montos: solo columnas numéricas (si alguna fuera texto se intenta convertir; lo que no se pueda sale en 0)
        string N(string col) => !t.TryGetValue(col, out var ty) ? "0"
                               : EsNumero(ty) ? $"ISNULL(C.[{col}], 0)"
                               : $"ISNULL(TRY_CONVERT(DECIMAL(18,2), C.[{col}]), 0)";
        string S(string col) => t.ContainsKey(col) ? $"ISNULL(CONVERT(NVARCHAR(60), C.[{col}]), N'')" : "N''";
        bool conNro = t.ContainsKey("NroCierre");
        bool hayDenominaciones = den.ContainsKey("NroCierre") && den.ContainsKey("CodMoneda") && den.ContainsKey("Total");

        // sin NroCierre: se arma un identificador con caja + apertura + fecha
        string nro = conNro ? "CONVERT(NVARCHAR(60), C.NroCierre)"
                            : $"{S("CodCaja")} + N'-' + {S("CodApertura")} + N'-' + CONVERT(NVARCHAR(19), C.FechaCierre, 120)";
        // Los billetes son de la APERTURA (KLK_CIERRE_DENOMINACION.NroCierre = NroCierre del cierre Z = CodApertura).
        // En KLK_CIERRE_X el NroCierre es un correlativo numérico; el enlace es X.CodApertura. Una apertura puede tener
        // varios cierres X: cada uno trae los billetes de toda la apertura y el complemento los cuenta una sola vez.
        // Texto contra número nunca coincide (y SQL Server fallaría al convertir): en ese caso no se enlazan.
        string? colEnlace = !hayDenominaciones ? null
            : t.ContainsKey("CodApertura") && EsTexto(t["CodApertura"]) == EsTexto(den["NroCierre"]) ? "CodApertura"
            : conNro && EsTexto(t["NroCierre"]) == EsTexto(den["NroCierre"]) ? "NroCierre" : null;
        bool enlazarBilletes = colEnlace != null;
        string enviado = t.ContainsKey("Enviado") ? "CASE WHEN CONVERT(NVARCHAR(10), C.Enviado) IN (N'1', N'S', N'True') THEN 1 ELSE 0 END" : "1";
        string enviadoStda = t.ContainsKey("EnviadoSTDA") ? "CASE WHEN CONVERT(NVARCHAR(10), C.EnviadoSTDA) IN (N'1', N'S', N'True') THEN 1 ELSE 0 END" : "1";
        var cols = string.Join(",\n       ", Formas.Select(f => $"{N(f.Col)}, {N(f.Col + "1")}, {N(f.Col + "2")}"));
        string nroD = enlazarBilletes ? NroComoDenominacion(colEnlace!, t[colEnlace!], den["NroCierre"]) : "NULL";
        string billetes = enlazarBilletes ? @"
OUTER APPLY (
    SELECT SUM(CASE WHEN D.CodMoneda = N'BS'  THEN D.Total END) AS ContadoBs,
           SUM(CASE WHEN D.CodMoneda = N'USD' THEN D.Total END) AS ContadoUSD,
           SUM(CASE WHEN D.CodMoneda = N'EUR' THEN D.Total END) AS ContadoEUR,
           SUM(CASE WHEN D.CodMoneda = N'COP' THEN D.Total END) AS ContadoCOP,
           SUM(CASE WHEN ABS(ISNULL(D.Total,0) - ISNULL(D.ValorMoneda,0) * ISNULL(D.Cantidad,0)) > 0.01 THEN 1 ELSE 0 END) AS MalCalculadas,
           SUM(CASE WHEN D.Cantidad < 0 OR D.Cantidad <> FLOOR(D.Cantidad) THEN 1 ELSE 0 END) AS CantidadesRaras,
           COUNT(*) AS Lineas
    FROM KLK_CIERRE_DENOMINACION D
    WHERE D.NroCierre = §NROD§
) D" : @"
OUTER APPLY (SELECT CAST(NULL AS DECIMAL(18,2)) AS ContadoBs, CAST(NULL AS DECIMAL(18,2)) AS ContadoUSD, CAST(NULL AS DECIMAL(18,2)) AS ContadoEUR,
                    CAST(NULL AS DECIMAL(18,2)) AS ContadoCOP, 0 AS MalCalculadas, 0 AS CantidadesRaras, -1 AS Lineas) D";
        string arqueos = enlazarBilletes ? $@"
SELECT D.NroCierre, D.CodMoneda, ISNULL(D.ValorMoneda, 0), ISNULL(D.Cantidad, 0), ISNULL(D.Total, 0)
FROM KLK_CIERRE_DENOMINACION D
WHERE D.NroCierre IN (SELECT {nroD} FROM {TablaCierres} C WHERE C.FechaCierre >= @ini AND C.FechaCierre < @fin)
  AND (ISNULL(D.Cantidad, 0) <> 0 OR ISNULL(D.Total, 0) <> 0)
ORDER BY D.NroCierre, D.CodMoneda, D.ValorMoneda DESC;" : "SELECT TOP 0 N'' , N'', 0, 0, 0;";
        billetes = billetes.Replace("§NROD§", nroD);

        // ---- ventas sin cierre X: facturas de una apertura + cajero que no tienen cierre X de esa apertura y ese cajero ----
        // (por apertura y no por día: un turno que pasa la medianoche cierra en la apertura correcta)
        bool sinCierreOk = t.ContainsKey("CodApertura") && fh.ContainsKey("CodApertura") && fh.ContainsKey("CodigoVendedorPOS");
        string apIgual = Igual("X.CodApertura", Tipo(t, "CodApertura"), "FH.CodApertura", Tipo(fh, "CodApertura"));
        string cajIgual = Igual("X.CodVendedorPOS", Tipo(t, "CodVendedorPOS"), "FH.CodigoVendedorPOS", Tipo(fh, "CodigoVendedorPOS"));
        string sinCierre = !sinCierreOk ? "SELECT TOP 0 N'', N'', N'', N'', N'', N'', 0, 0, 0;" : $@"
SELECT ISNULL(CONVERT(NVARCHAR(60), FH.CodCaja), N''), ISNULL(CONVERT(NVARCHAR(60), FH.CodApertura), N''),
       ISNULL(CONVERT(NVARCHAR(60), FH.CodigoVendedorPOS), N''), ISNULL(MAX(U.Nombre), N''),
       CONVERT(CHAR(19), MIN(FH.FechaFactura), 120), CONVERT(CHAR(19), MAX(FH.FechaFactura), 120),
       COUNT(DISTINCT FH.NumFactura),
       SUM(ISNULL(FL.TotalLineaUsd,0) + ISNULL(FL.IVAUSD,0)
           - (ISNULL(FL.DescuentoUSD,0) + ISNULL(FL.MontoDescuentoFidelUsd,0) + ISNULL(FL.MontoDescuentoFormPagoUsd,0))),
       CASE WHEN EXISTS (SELECT 1 FROM {TablaCierres} X WHERE {apIgual}) THEN 1 ELSE 0 END
FROM KLK_FACTURAHDR FH
JOIN KLK_FACTURALINE FL ON FL.NumFactura = FH.NumFactura AND FL.CodCaja = FH.CodCaja AND FL.CodApertura = FH.CodApertura
LEFT JOIN KLK_USUARIOS U ON {Igual("U.Id_Usuario", Tipo(usu, "Id_Usuario"), "FH.CodigoVendedorPOS", Tipo(fh, "CodigoVendedorPOS"))}
WHERE FH.EnEspera = 0 AND FH.FechaFactura >= @ini AND FH.FechaFactura < @fin
  AND NOT EXISTS (SELECT 1 FROM {TablaCierres} X WHERE {apIgual} AND {cajIgual})
GROUP BY FH.CodCaja, FH.CodApertura, FH.CodigoVendedorPOS
ORDER BY MIN(FH.FechaFactura);";
        string vendedor = t.ContainsKey("CodVendedorPOS") ? "C.CodVendedorPOS" : "NULL";
        return $@"
SET NOCOUNT ON;
DECLARE @ini DATETIME = @p1, @fin DATETIME = DATEADD(DAY, 1, @p2);
SELECT {nro}, CONVERT(CHAR(19), C.FechaCierre, 120), {S("CodCaja")}, {S("CodApertura")},
       {S("CodVendedorPOS")}, ISNULL(U.Nombre, N''), {N("TasaDeCambio")},
       {enviado}, {enviadoStda},
       {N("Total")}, {N("Total1")}, {N("Total2")},
       ISNULL(D.ContadoBs, 0), ISNULL(D.ContadoUSD, 0), ISNULL(D.ContadoEUR, 0), ISNULL(D.ContadoCOP, 0),
       ISNULL(D.MalCalculadas, 0), ISNULL(D.CantidadesRaras, 0), ISNULL(D.Lineas, 0),
       ISNULL(F.Usd, 0) - ISNULL(N.Usd, 0), ISNULL(F.Docs, 0), ISNULL(N.Docs, 0),
       {cols}
FROM {TablaCierres} C
LEFT JOIN KLK_USUARIOS U ON {Igual("U.Id_Usuario", Tipo(usu, "Id_Usuario"), vendedor, Tipo(t, "CodVendedorPOS"))}{billetes}
-- Recaudado según documentos (mismo criterio que getVentasPosStore: caja + cajero + día del cierre),
-- en USD con la fórmula de las ventas; el procedimiento original restaba descuentos USD a montos en Bs.
CROSS APPLY (SELECT CAST(C.FechaCierre AS DATE) AS Dia, DATEADD(DAY, 1, CAST(C.FechaCierre AS DATE)) AS Sig) T
OUTER APPLY (
    SELECT COUNT(DISTINCT FH.NumFactura) AS Docs,
           SUM(ISNULL(FL.TotalLineaUsd,0) + ISNULL(FL.IVAUSD,0)
               - (ISNULL(FL.DescuentoUSD,0) + ISNULL(FL.MontoDescuentoFidelUsd,0) + ISNULL(FL.MontoDescuentoFormPagoUsd,0))) AS Usd
    FROM KLK_FACTURAHDR FH
    JOIN KLK_FACTURALINE FL ON FL.NumFactura = FH.NumFactura AND FL.CodCaja = FH.CodCaja AND FL.CodApertura = FH.CodApertura
    WHERE {Igual("FH.CodCaja", Tipo(fh, "CodCaja"), "C.CodCaja", Tipo(t, "CodCaja"))}
      AND {Igual("FH.CodigoVendedorPOS", Tipo(fh, "CodigoVendedorPOS"), vendedor, Tipo(t, "CodVendedorPOS"))} AND FH.EnEspera = 0
      AND FH.FechaFactura >= T.Dia AND FH.FechaFactura < T.Sig
) F
OUTER APPLY (
    SELECT COUNT(DISTINCT NH.NumNota) AS Docs,
           SUM(ISNULL(NL.TotalLineaUsd,0) + ISNULL(NL.IVAUSD,0)
               - (ISNULL(NL.DescuentoUSD,0) + ISNULL(NL.MontoDescuentoFidelUsd,0) + ISNULL(NL.MontoDescuentoFormPagoUsd,0))) AS Usd
    FROM KLK_NCREDITOHDR NH
    JOIN KLK_NCREDITOLINE NL ON NL.NumNota = NH.NumNota AND NL.CodCaja = NH.CodCaja
                            AND {Igual("NH.CodApertura", Tipo(nh, "CodApertura"), "NL.Orden", Tipo(nl, "Orden"))}
    WHERE {Igual("NH.CodCaja", Tipo(nh, "CodCaja"), "C.CodCaja", Tipo(t, "CodCaja"))}
      AND {Igual("NH.CodigoVendedorPOS", Tipo(nh, "CodigoVendedorPOS"), vendedor, Tipo(t, "CodVendedorPOS"))}
      AND NH.FechaNota >= T.Dia AND NH.FechaNota < T.Sig
) N
WHERE C.FechaCierre >= @ini AND C.FechaCierre < @fin
ORDER BY C.FechaCierre, C.CodCaja;
{arqueos}
{sinCierre}";
    }

    public static async Task<ResultadoCierres> EjecutarAsync(string cadena, DateTime desde, DateTime hasta, int timeoutSeg, CancellationToken ct)
    {
        var cierres = new List<object?[]>();
        var arqueos = new List<object?[]>();
        var sinCierre = new List<object?[]>();
        await using var conn = new SqlConnection(cadena);
        await conn.OpenAsync(ct);
        var t = await ColumnasAsync(conn, cadena, TablaCierres, timeoutSeg, ct);
        if (t.Count == 0) throw new InvalidOperationException($"La tienda no tiene la tabla {TablaCierres}.");
        foreach (var obligatoria in new[] { "FechaCierre", "CodCaja", "Total", "Total1" })
            if (!t.ContainsKey(obligatoria)) throw new InvalidOperationException($"La tabla {TablaCierres} no tiene la columna {obligatoria}.");
        var den = await ColumnasAsync(conn, cadena, "KLK_CIERRE_DENOMINACION", timeoutSeg, ct);
        var usu = await ColumnasAsync(conn, cadena, "KLK_USUARIOS", timeoutSeg, ct);
        var fh = await ColumnasAsync(conn, cadena, "KLK_FACTURAHDR", timeoutSeg, ct);
        var nh = await ColumnasAsync(conn, cadena, "KLK_NCREDITOHDR", timeoutSeg, ct);
        var nl = await ColumnasAsync(conn, cadena, "KLK_NCREDITOLINE", timeoutSeg, ct);

        await using var cmd = new SqlCommand(Lectura.Prefijo + SqlCierres(t, den, usu, fh, nh, nl), conn) { CommandTimeout = timeoutSeg };
        cmd.Parameters.AddWithValue("@p1", desde.Date);
        cmd.Parameters.AddWithValue("@p2", hasta.Date);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var f = new object?[rd.FieldCount];
            for (int i = 0; i < 6; i++) f[i] = rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i))!.Trim();
            for (int i = 6; i < rd.FieldCount; i++) f[i] = rd.IsDBNull(i) ? 0m : Convert.ToDecimal(rd.GetValue(i));
            cierres.Add(f);
        }
        if (await rd.NextResultAsync(ct))
            while (await rd.ReadAsync(ct))
                arqueos.Add(new object?[]
                {
                    Convert.ToString(rd.GetValue(0))!.Trim(), Convert.ToString(rd.GetValue(1))!.Trim(),
                    Convert.ToDecimal(rd.GetValue(2)), Convert.ToDecimal(rd.GetValue(3)), Convert.ToDecimal(rd.GetValue(4))
                });
        if (await rd.NextResultAsync(ct))
            while (await rd.ReadAsync(ct))
            {
                var f = new object?[9];
                for (int i = 0; i < 6; i++) f[i] = rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i))!.Trim();
                for (int i = 6; i < 9; i++) f[i] = rd.IsDBNull(i) ? 0m : Convert.ToDecimal(rd.GetValue(i));
                sinCierre.Add(f);
            }
        bool disponible = t.ContainsKey("CodApertura") && fh.ContainsKey("CodApertura") && fh.ContainsKey("CodigoVendedorPOS");
        return new ResultadoCierres(Formas.Select(x => x.Nombre).ToArray(), Formas.Select(x => x.Signo).ToArray(), cierres, arqueos,
                                    sinCierre, disponible, TablaCierres);
    }
}
