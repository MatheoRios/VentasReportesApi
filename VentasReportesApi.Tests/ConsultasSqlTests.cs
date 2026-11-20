using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VentasReportesApi.Tests;

[TestClass]
public class ConsultasSqlTests
{
    [TestMethod]
    public void UltimosCostosSqlInsertLoteUsaSoloParametros()
    {
        string sql = ConsultaUltimosCostos.SqlInsertLote(3);
        Assert.AreEqual("INSERT INTO #O VALUES (@n0,@c0),(@n1,@c1),(@n2,@c2);", sql);
    }

    [TestMethod]
    public void CategoriasAplicaFiltrosANotasCuandoExistenYUsaTryConvertSiDifierenTipos()
    {
        var nhConCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CodApertura"] = "int", ["NoFiscal"] = "bit", ["FacturaACredito"] = "bit"
        };
        var nlTexto = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Orden"] = "nvarchar" };

        string sqlCon = ConsultaCategorias.ConstruirSql(nhConCols, nlTexto);
        Assert.IsTrue(sqlCon.Contains("TRY_CONVERT(BIGINT, N.Orden) = NH.CodApertura"));
        Assert.IsTrue(sqlCon.Contains("NH.NoFiscal = @noFiscal"));
        Assert.IsTrue(sqlCon.Contains("NH.FacturaACredito = @credito"));

        var nhSinCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CodApertura"] = "int" };
        string sqlSin = ConsultaCategorias.ConstruirSql(nhSinCols, nlTexto);
        Assert.IsFalse(sqlSin.Contains("NH.NoFiscal"));
        Assert.IsFalse(sqlSin.Contains("NH.FacturaACredito"));
    }

    [TestMethod]
    public void ResumenDiariaYProductosMesUsanIgualEnNotasDeCredito()
    {
        var nh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CodApertura"] = "int" };
        var nl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Orden"] = "varchar" };

        string sqlRes = ConsultaResumen.ConstruirSql(nh, nl);
        string sqlDia = ConsultaDiaria.ConstruirSql(nh, nl);
        string sqlProd = ConsultaProductosMes.ConstruirSqlLineas(nh, nl);

        Assert.IsTrue(sqlRes.Contains("NH.CodApertura = TRY_CONVERT(BIGINT, NL.Orden)"));
        Assert.IsTrue(sqlDia.Contains("NH.CodApertura = TRY_CONVERT(BIGINT, NL.Orden)"));
        Assert.IsTrue(sqlProd.Contains("TRY_CONVERT(BIGINT, N.Orden) = NH.CodApertura"));
    }

    [TestMethod]
    public void CierresPreAgregaEnTablasTemporalesYNoDependeDeCulturaEsVe()
    {
        var culturaAnterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-VE");
            var t = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["FechaCierre"] = "datetime", ["CodCaja"] = "nvarchar", ["CodApertura"] = "int",
                ["CodVendedorPOS"] = "int", ["Total"] = "decimal", ["Total1"] = "decimal"
            };
            var den = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NroCierre"] = "int", ["CodMoneda"] = "nvarchar", ["Total"] = "decimal"
            };
            var usu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Id_Usuario"] = "int" };
            var fh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CodCaja"] = "nvarchar", ["CodApertura"] = "int", ["CodigoVendedorPOS"] = "int"
            };
            var nh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CodCaja"] = "nvarchar", ["CodApertura"] = "int", ["CodigoVendedorPOS"] = "int"
            };
            var nl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Orden"] = "nvarchar" };

            string sql = ConsultaCierres.SqlCierres(t, den, usu, fh, nh, nl);
            Assert.IsTrue(sql.Contains("INTO #F"));
            Assert.IsTrue(sql.Contains("INTO #N"));
            Assert.IsTrue(sql.Contains("LEFT JOIN #F F ON"));
            Assert.IsTrue(sql.Contains("LEFT JOIN #N N ON"));
            Assert.IsTrue(sql.Contains("DROP TABLE #F; DROP TABLE #N;"));
            Assert.IsFalse(sql.Contains("OUTER APPLY (\n    SELECT COUNT(DISTINCT FH.NumFactura)"));
            Assert.IsFalse(sql.Contains("0,01"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culturaAnterior;
        }
    }
}
