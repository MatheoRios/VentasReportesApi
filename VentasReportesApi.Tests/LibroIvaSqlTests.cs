using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VentasReportesApi.Tests;

[TestClass]
public class LibroIvaSqlTests
{
    [TestMethod]
    public void SqlGeneradoUsaPuntoDecimalInclusoEnCulturaEsVe()
    {
        var culturaAnterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-VE");
            var cols = new Dictionary<string, Dictionary<string, string>>
            {
                ["KLK_FACTURAHDR"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["FechaFactura"] = "datetime", ["EnEspera"] = "bit", ["NumTicketFiscal"] = "nvarchar",
                    ["NCorteZ"] = "nvarchar", ["Exento"] = "decimal", ["MontoImpuesto"] = "decimal",
                    ["Total"] = "decimal", ["MontoIGTF"] = "decimal", ["Retencion"] = "decimal",
                    ["TasaUSD"] = "decimal", ["NoFiscal"] = "bit"
                },
                ["KLK_FACTURALINE"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["NumFactura"] = "nvarchar", ["PorcImpuesto"] = "decimal", ["TotalLinea"] = "decimal", ["MontoImpuesto"] = "decimal"
                },
                ["KLK_NCREDITOHDR"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["FechaNota"] = "datetime", ["NumTicketFiscal"] = "nvarchar", ["NCorteZ"] = "nvarchar",
                    ["Exento"] = "decimal", ["MontoImpuesto"] = "decimal", ["Total"] = "decimal",
                    ["MontoIGTF"] = "decimal", ["Retencion"] = "decimal", ["TasaUSD"] = "decimal",
                    ["NoFiscal"] = "bit", ["FacturaAfectada"] = "nvarchar"
                },
                ["KLK_NCREDITOLINE"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["NumNota"] = "nvarchar", ["PorcImpuesto"] = "decimal", ["TotalLinea"] = "decimal",
                    ["MontoImpuesto"] = "decimal", ["Orden"] = "nvarchar"
                },
                ["KLK_CAJA"] = new(StringComparer.OrdinalIgnoreCase) { ["CodCaja"] = "nvarchar", ["SerialImpresora"] = "nvarchar" },
                ["KLK_CIERRE"] = new(StringComparer.OrdinalIgnoreCase) { ["CodCaja"] = "nvarchar", ["CodApertura"] = "nvarchar", ["SerialImpresora"] = "nvarchar" }
            };
            var avisos = new List<string>();
            string sql = ConsultaLibroIva.Sql(cols, avisos);

            Assert.IsTrue(sql.Contains("BETWEEN 15.5 AND 16.5"));
            Assert.IsTrue(sql.Contains("BETWEEN 7.5 AND 8.5"));
            Assert.IsTrue(sql.Contains("BETWEEN 30.5 AND 31.5"));
            Assert.IsFalse(sql.Contains("15,5"));
            Assert.IsFalse(sql.Contains("7,5"));
            Assert.IsFalse(sql.Contains("30,5"));
            Assert.AreEqual(0, avisos.Count);
        }
        finally
        {
            CultureInfo.CurrentCulture = culturaAnterior;
        }
    }
}
