using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VentasReportesApi.Tests;

[TestClass]
public class SeguridadYTokensTests
{
    private string tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "VentasReportesApiTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
    }

    [TestMethod]
    public void ClavesHashYVerificacionFuncionanYUsanSalUnica()
    {
        string h1 = Claves.Hash("ClaveSegura123!");
        string h2 = Claves.Hash("ClaveSegura123!");

        Assert.AreNotEqual(h1, h2);
        Assert.IsTrue(Claves.Verificar("ClaveSegura123!", h1));
        Assert.IsTrue(Claves.Verificar("ClaveSegura123!", h2));
        Assert.IsFalse(Claves.Verificar("ClaveIncorrecta!", h1));
        Assert.IsFalse(Claves.Verificar("ClaveSegura123!", "formato-invalido"));
    }

    [TestMethod]
    public void ClavesPoliticaExigeMinimo8Caracteres()
    {
        Assert.IsNotNull(Claves.ValidarPolitica(""));
        Assert.IsNotNull(Claves.ValidarPolitica("1234567"));
        Assert.IsNull(Claves.ValidarPolitica("12345678"));
    }

    [TestMethod]
    public void TokensValidanFirmaExpiracionYTokenVersion()
    {
        var tokens = new Tokens(Path.Combine(tempDir, "token.key"));
        var (tok, _) = tokens.Crear("jperez", 2.0, tokenVersion: 5);

        var res = tokens.ValidarConVersion(tok);
        Assert.IsNotNull(res);
        Assert.AreEqual("jperez", res.Value.login);
        Assert.AreEqual(5L, res.Value.tokenVersion);

        // Alterar la firma invalida el token
        string alterado = tok[..^1] + (tok[^1] == 'A' ? 'B' : 'A');
        Assert.IsNull(tokens.ValidarConVersion(alterado));

        // Token vencido se rechaza
        var (vencido, _) = tokens.Crear("jperez", -0.1, tokenVersion: 1);
        Assert.IsNull(tokens.ValidarConVersion(vencido));
    }

    [TestMethod]
    public void CifradoAesGcmProtegeYDetectaManipulacion()
    {
        var cif = new Cifrado(Path.Combine(tempDir, "tiendas.key"));
        string c1 = cif.Cifrar("MiClaveSql#2026");
        string c2 = cif.Cifrar("MiClaveSql#2026");

        Assert.AreNotEqual(c1, c2);
        Assert.AreEqual("MiClaveSql#2026", cif.Descifrar(c1));
        Assert.AreEqual("MiClaveSql#2026", cif.Descifrar(c2));

        byte[] raw = Convert.FromBase64String(c1);
        raw[^1] ^= 0xFF;
        Assert.ThrowsException<AuthenticationTagMismatchException>(() => cif.Descifrar(Convert.ToBase64String(raw)));
    }
}
