using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VentasReportesApi.Tests;

[TestClass]
public class AlmacenYPermisosTests
{
    private string tempDir = null!;
    private Almacen db = null!;

    [TestInitialize]
    public void Init()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "VentasReportesApiDb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        db = new Almacen(Path.Combine(tempDir, "seguridad.db"));
        db.Inicializar();
        db.Migrar();
        db.InicializarTiendas();
        db.InicializarHuellas();
        db.InicializarChat(365);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
    }

    [TestMethod]
    public void TokenVersionSeIncrementaAlCambiarClaveRevocarODesactivar()
    {
        db.CrearUsuario("ana", "Ana Pérez", "TIENDA", "ClaveInicial1");
        var u1 = db.ObtenerUsuario("ana")!;
        Assert.AreEqual(1L, u1.TokenVersion);

        db.CambiarClave("ana", "ClaveNueva123");
        var u2 = db.ObtenerUsuario("ana")!;
        Assert.AreEqual(2L, u2.TokenVersion);

        db.RevocarSesiones("ana");
        var u3 = db.ObtenerUsuario("ana")!;
        Assert.AreEqual(3L, u3.TokenVersion);

        db.Activar("ana", false);
        var u4 = db.ObtenerUsuario("ana")!;
        Assert.IsFalse(u4.Activo);
        Assert.AreEqual(4L, u4.TokenVersion);
    }

    [TestMethod]
    public void BloqueoPorIntentosFallidosYDesbloqueoEnLoginExitoso()
    {
        db.CrearUsuario("carlos", "Carlos Ruiz", "TIENDA", "ClaveSegura1");
        var u = db.ObtenerUsuario("carlos")!;

        db.RegistrarFallo(u.Id, intentos: 2, minutos: 15);
        Assert.IsNull(db.ObtenerUsuario("carlos")!.BloqueadoHasta);

        db.RegistrarFallo(u.Id, intentos: 2, minutos: 15);
        var bloqueado = db.ObtenerUsuario("carlos")!;
        Assert.IsNotNull(bloqueado.BloqueadoHasta);
        Assert.IsTrue(bloqueado.BloqueadoHasta > DateTime.Now);

        db.LoginExitoso(u.Id);
        var libre = db.ObtenerUsuario("carlos")!;
        Assert.AreEqual(0, libre.Fallos);
        Assert.IsNull(libre.BloqueadoHasta);
    }

    [TestMethod]
    public void RolesYPermisosRespetanJerarquiaYDefaultsSensibles()
    {
        var admin = new Usuario { Login = "root", Rol = "ADMIN" };
        var jefe = new Usuario { Login = "jefe1", Rol = "JEFE" };
        var reg = new Usuario { Login = "reg1", Rol = "REGIONAL" };
        var tienda = new Usuario { Login = "t1", Rol = "TIENDA" };

        Assert.IsTrue(admin.PuedeEditar(jefe));
        Assert.IsFalse(jefe.PuedeEditar(admin));
        Assert.IsFalse(jefe.PuedeEditar(jefe));
        Assert.IsTrue(jefe.PuedeEditar(reg));
        Assert.IsTrue(jefe.PuedeEditar(tienda));

        // Por defecto (Permisos == null) NO incluye compras ni impuestos; ADMIN siempre tiene todos
        Assert.IsTrue(admin.Tiene("compras"));
        Assert.IsTrue(admin.Tiene("impuestos"));
        Assert.IsFalse(tienda.Tiene("compras"));
        Assert.IsFalse(tienda.Tiene("impuestos"));
        Assert.IsTrue(tienda.Tiene("ventas"));
    }

    [TestMethod]
    public void RenombrarTiendaActualizaAsignacionesDeUsuarios()
    {
        db.CrearUsuario("luis", "Luis Gómez", "TIENDA", "ClaveSegura1");
        var u = db.ObtenerUsuario("luis")!;
        db.CrearTienda(new Tienda { Nombre = "BOSQUE", Servidor = "10.0.0.1", BaseDatos = "POS", Usuario = "usr" }, "cif");
        db.AsignarSucursales(u.Id, new[] { "BOSQUE" }, true);

        db.ActualizarTienda("BOSQUE", new Tienda { Nombre = "BOSQUE_SUR", Servidor = "10.0.0.1", BaseDatos = "POS", Usuario = "usr" }, null);

        var asignadas = db.SucursalesDe(u.Id);
        CollectionAssert.AreEqual(new[] { "BOSQUE_SUR" }, asignadas);
    }
}
