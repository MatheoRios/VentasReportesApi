// Generado al dividir Program.cs por módulos: el código de cada parte es el mismo que estaba en Program.cs.
namespace VentasReportesApi;

public sealed partial class Servidor
{
    private void MapearSesion()
    {
        // ------------------------------------------------------------------ endpoints
        app.MapGet("/api/salud", () => Results.Ok(new { estado = "ok", version, hora = DateTime.Now }));

        // Hash ficticio para igualar el costo de PBKDF2 cuando el usuario no existe o está inactivo
        string hashFicticio = Claves.Hash("clave-ficticia-tiempo-constante");

        app.MapPost("/api/login", (LoginPeticion p, HttpContext ctx) =>
        {
            string ipCliente = Ip(ctx);
            if (!PermitirIntentoLoginIp(ipCliente))
            {
                almacen.Auditar(p.Usuario, "LOGIN", resultado: "Demasiados intentos desde esta IP", ip: ipCliente);
                return Error(429, "Demasiados intentos de ingreso desde este equipo. Espere un minuto e intente de nuevo.");
            }
            var u = almacen.ObtenerUsuario(p.Usuario);
            if (u == null || !u.Activo || string.IsNullOrEmpty(p.Clave))
            {
                if (!string.IsNullOrEmpty(p.Clave)) Claves.Verificar(p.Clave, hashFicticio);
                almacen.Auditar(p.Usuario, "LOGIN", resultado: u == null ? "Usuario inexistente" : "Usuario inactivo", ip: ipCliente);
                return Error(401, "Usuario o clave incorrectos.");
            }
            if (u.BloqueadoHasta > DateTime.Now)
            {
                almacen.Auditar(u.Login, "LOGIN", resultado: "Bloqueado", ip: ipCliente);
                return Error(401, $"Usuario bloqueado por intentos fallidos. Intente después de las {u.BloqueadoHasta:HH:mm}.");
            }
            if (!Claves.Verificar(p.Clave, u.HashClave))
            {
                almacen.RegistrarFallo(u.Id, intentos, minutosBloqueo);
                almacen.Auditar(u.Login, "LOGIN", resultado: "Clave incorrecta", ip: ipCliente);
                return Error(401, "Usuario o clave incorrectos.");
            }

            almacen.LoginExitoso(u.Id);
            var (token, expira) = tokens.Crear(u.Login, horasSesion, u.TokenVersion);
            almacen.Auditar(u.Login, "LOGIN", resultado: "OK", ip: ipCliente);
            return Results.Ok(new { token, usuario = u.Login, nombre = u.Nombre, rol = u.Rol, expira = expira.ToLocalTime(),
                                    debeCambiarClave = u.DebeCambiarClave, permisos = u.ListaPermisos });
        });

        app.MapPost("/api/logout", (HttpContext ctx) =>
        {
            var u = Autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            almacen.RevocarSesiones(u.Login);
            almacen.Auditar(u.Login, "LOGIN", resultado: "Cerró sesión", ip: Ip(ctx));
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/sucursales", (HttpContext ctx) =>
        {
            var u = Autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (u.DebeCambiarClave) return Error(403, ClaveTemporal);
            return Results.Ok(new { sucursales = Permitidas(u), nombre = u.Nombre, rol = u.Rol, permisos = u.ListaPermisos });
        });
    }

    private void MapearMiCuenta()
    {
        // ------------------------------------------------------------------ mi cuenta
        app.MapPost("/api/mi-clave", (CambioClavePeticion p, HttpContext ctx) =>
        {
            var u = Autenticar(ctx);
            if (u == null) return Error(401, "Sesión vencida o inválida.");
            if (string.IsNullOrEmpty(p.ClaveActual) || !Claves.Verificar(p.ClaveActual, u.HashClave))
            {
                almacen.Auditar(u.Login, "CLAVE", resultado: "Clave actual incorrecta", ip: Ip(ctx));
                return Error(400, "La clave actual no es correcta.");
            }
            var problema = Claves.ValidarPolitica(p.ClaveNueva ?? "");
            if (problema != null) return Error(400, problema);
            if (p.ClaveNueva == p.ClaveActual) return Error(400, "La clave nueva debe ser distinta de la actual.");
            almacen.CambiarClave(u.Login, p.ClaveNueva!);
            almacen.MarcarCambioClave(u.Login, false);
            var actualizado = almacen.ObtenerUsuario(u.Login);
            var (token, expira) = tokens.Crear(u.Login, horasSesion, actualizado?.TokenVersion ?? (u.TokenVersion + 1));
            almacen.Auditar(u.Login, "CLAVE", resultado: "Cambió su clave", ip: Ip(ctx));
            return Results.Ok(new { ok = true, token, expira = expira.ToLocalTime() });
        });

    }
}
