using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VentasReportesApi.Tests;

[TestClass]
public class ConcurrenciaYImportadorTests
{
    [TestMethod]
    public async Task ConcurrenciaRespetaLimitePorTiendaYRetornaNullAlVencerEspera()
    {
        var c = new Concurrencia(global: 10, porTienda: 1, porUsuario: 5, espera: TimeSpan.FromMilliseconds(80));
        using var t1 = await c.EntrarAsync("BOSQUE", "u1", CancellationToken.None);
        Assert.IsNotNull(t1);

        var t2 = await c.EntrarAsync("BOSQUE", "u2", CancellationToken.None);
        Assert.IsNull(t2);

        using var t3 = await c.EntrarAsync("CENTRO", "u2", CancellationToken.None);
        Assert.IsNotNull(t3);
    }

    [TestMethod]
    public async Task CompartidosUneConsultasEnVueloYUsaCache()
    {
        var comp = new Compartidos(pesoMaximo: 1000);
        int ejecuciones = 0;
        var listo = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Consulta(CancellationToken ct)
        {
            Interlocked.Increment(ref ejecuciones);
            await listo.Task.WaitAsync(ct);
            return "resultado-ok";
        }

        var p1 = comp.ObtenerAsync("k1", TimeSpan.FromMinutes(5), Consulta, _ => 10, 500, CancellationToken.None);
        var p2 = comp.ObtenerAsync("k1", TimeSpan.FromMinutes(5), Consulta, _ => 10, 500, CancellationToken.None);
        listo.SetResult(true);

        var r1 = await p1;
        var r2 = await p2;
        Assert.AreEqual(1, ejecuciones);
        Assert.AreEqual("resultado-ok", r1.valor);
        Assert.AreEqual("resultado-ok", r2.valor);
        Assert.IsTrue(r1.origen == OrigenResultado.Consulta || r2.origen == OrigenResultado.Consulta);
        Assert.IsTrue(r1.origen == OrigenResultado.Compartido || r2.origen == OrigenResultado.Compartido);

        var r3 = await comp.ObtenerAsync("k1", TimeSpan.FromMinutes(5), Consulta, _ => 10, 500, CancellationToken.None);
        Assert.AreEqual(1, ejecuciones);
        Assert.AreEqual(OrigenResultado.Cache, r3.origen);
    }

    [TestMethod]
    public void ImportadorConfigLeeCadenasValidasYRechazaDtd()
    {
        string xmlValido = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <connectionStrings>
                <add name="BOSQUE" connectionString="Server=tcp:192.168.1.10,1433;Database=POS;User Id=rep;Password=abc;" />
              </connectionStrings>
            </configuration>
            """;
        var errores = new List<string>();
        var leidas = ImportadorConfig.Leer(xmlValido, errores);
        Assert.AreEqual(0, errores.Count);
        Assert.AreEqual(1, leidas.Count);
        Assert.AreEqual("BOSQUE", leidas[0].Nombre);
        Assert.AreEqual("192.168.1.10", leidas[0].Servidor);
        Assert.AreEqual(1433, leidas[0].Puerto);

        string xmlConDtd = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [ <!ENTITY lol "lol"> ]>
            <configuration><connectionStrings/></configuration>
            """;
        var errDtd = new List<string>();
        var resDtd = ImportadorConfig.Leer(xmlConDtd, errDtd);
        Assert.AreEqual(0, resDtd.Count);
        Assert.IsTrue(errDtd.Count > 0);
    }

    [TestMethod]
    public async Task CompartidosMantienePesoExactoAlSobrescribirYRecortar()
    {
        var comp = new Compartidos(pesoMaximo: 100);
        await comp.ObtenerAsync("a", TimeSpan.FromMinutes(5), _ => Task.FromResult("v1"), _ => 40, 100, CancellationToken.None);
        Assert.AreEqual(40, comp.PesoActual);

        // al expirar o entrar otra clave, el recorte mantiene pesoActual exacto
        await comp.ObtenerAsync("b", TimeSpan.FromMinutes(5), _ => Task.FromResult("v2"), _ => 40, 100, CancellationToken.None);
        Assert.AreEqual(80, comp.PesoActual);

        await comp.ObtenerAsync("c", TimeSpan.FromMinutes(5), _ => Task.FromResult("v3"), _ => 50, 100, CancellationToken.None);
        Assert.IsTrue(comp.PesoActual <= 100);
        Assert.AreEqual(90, comp.PesoActual);
    }

    [TestMethod]
    public void ChatIdPrivadoEsSimetricoEInsensibleAMayusculas()
    {
        Assert.AreEqual("dm:ana|juan", ChatApi.IdPrivado("Juan", "ANA"));
        Assert.AreEqual(ChatApi.IdPrivado("ana", "juan"), ChatApi.IdPrivado("JUAN", "Ana"));
    }
}
