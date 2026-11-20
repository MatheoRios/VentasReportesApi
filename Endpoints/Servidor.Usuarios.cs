// Generado al dividir Program.cs por módulos: el código de cada parte es el mismo que estaba en Program.cs.
namespace VentasReportesApi;

public sealed partial class Servidor
{
    private void MapearUsuarios()
    {

        adminApi.MapGet("/usuarios", (HttpContext ctx) =>
        {
            var r = ExigirGestor(ctx, out var quien);
            if (r != null) return r;
            return Results.Ok(new { usuarios = almacen.ListarUsuarios().Select(x => VistaUsuario(x, quien)).ToList() });
        });

        adminApi.MapGet("/sucursales", (HttpContext ctx) =>
        {
            var r = ExigirGestor(ctx, out _);
            if (r != null) return r;
            return Results.Ok(new { sucursales = TiendasActivas().OrderBy(k => k, StringComparer.CurrentCulture).ToList() });
        });

        adminApi.MapPost("/usuarios", (UsuarioPeticion p, HttpContext ctx) =>
        {
            var r = ExigirGestor(ctx, out var admin);
            if (r != null) return r;
            var err = Validar(p, true, out var rol, out var tiendas);
            if (err != null) return Error(400, err);
            if (!admin.EsAdmin && !Almacen.RolesDeJefe.Contains(rol)) return Error(403, SinPermisoJefe);
            string login = p.Login!.Trim();
            if (almacen.ObtenerUsuario(login) != null) return Error(409, $"Ya existe el usuario '{login}'.");

            almacen.CrearUsuario(login, p.Nombre!.Trim(), rol, p.Clave!);
            almacen.MarcarCambioClave(login, true);                         // clave temporal: la cambia al primer ingreso
            var nuevo = almacen.ObtenerUsuario(login)!;
            if (p.Activo == false) almacen.Activar(login, false);
            almacen.AsignarSucursales(nuevo.Id, rol is "ADMIN" or "JEFE" ? new List<string>() : tiendas, true);
            // solo el ADMIN decide los permisos de reportes; lo que crea un Jefe recibe los de siempre (todos)
            var permisosNuevos = admin.EsAdmin && p.Permisos != null ? Almacen.NormalizarPermisos(p.Permisos) : Almacen.PermisosPorDefecto.ToList();
            almacen.AsignarPermisos(nuevo.Id, permisosNuevos);
            almacen.Auditar(admin.Login, "USUARIO", null, null, null, null,
                $"Creó usuario {login} ({rol}) tiendas: {string.Join(",", tiendas)} permisos: {string.Join(",", permisosNuevos)}", Ip(ctx));
            return Results.Ok(new { usuario = VistaUsuario(almacen.ObtenerUsuario(login)!, admin) });
        });

        adminApi.MapPut("/usuarios/{login}", (string login, UsuarioPeticion p, HttpContext ctx) =>
        {
            var r = ExigirGestor(ctx, out var admin);
            if (r != null) return r;
            var u = almacen.ObtenerUsuario(login);
            if (u == null) return Error(404, $"No existe el usuario '{login}'.");
            var err = Validar(p, false, out var rol, out var tiendas);
            if (err != null) return Error(400, err);
            if (!admin.PuedeEditar(u) || (!admin.EsAdmin && !Almacen.RolesDeJefe.Contains(rol))) return Error(403, SinPermisoJefe);
            bool activo = p.Activo ?? u.Activo;

            bool esElMismo = u.Login.Equals(admin.Login, StringComparison.OrdinalIgnoreCase);
            if (esElMismo && (rol != "ADMIN" || !activo))
                return Error(400, "No puede quitarse a sí mismo el rol de administrador ni desactivarse.");
            if (u.EsAdmin && u.Activo && (rol != "ADMIN" || !activo) && almacen.ContarAdminsActivos(u.Id) == 0)
                return Error(400, "Debe quedar al menos un administrador activo.");

            almacen.ActualizarUsuario(u.Id, p.Nombre!.Trim(), rol, activo);
            if (!string.IsNullOrEmpty(p.Clave))
            {
                almacen.CambiarClave(u.Login, p.Clave);
                almacen.MarcarCambioClave(u.Login, !u.Login.Equals(admin.Login, StringComparison.OrdinalIgnoreCase));
                almacen.QuitarHuellasDe(u.Login);                              // clave restablecida: las huellas se activan de nuevo
            }
            almacen.AsignarSucursales(u.Id, rol is "ADMIN" or "JEFE" ? new List<string>() : tiendas, true);
            var permisos = admin.EsAdmin && p.Permisos != null ? Almacen.NormalizarPermisos(p.Permisos) : u.ListaPermisos;   // el Jefe no los cambia
            almacen.AsignarPermisos(u.Id, permisos);
            almacen.Auditar(admin.Login, "USUARIO", null, null, null, null,
                $"Editó usuario {u.Login} ({rol}, {(activo ? "activo" : "inactivo")}{(string.IsNullOrEmpty(p.Clave) ? "" : ", clave nueva")}) tiendas: {string.Join(",", tiendas)} permisos: {(rol == "ADMIN" ? "todos" : string.Join(",", permisos))}", Ip(ctx));
            return Results.Ok(new { usuario = VistaUsuario(almacen.ObtenerUsuario(u.Login)!, admin) });
        });

        adminApi.MapDelete("/usuarios/{login}", (string login, HttpContext ctx) =>
        {
            var r = ExigirGestor(ctx, out var admin);
            if (r != null) return r;
            var u = almacen.ObtenerUsuario(login);
            if (u == null) return Error(404, $"No existe el usuario '{login}'.");
            if (u.Login.Equals(admin.Login, StringComparison.OrdinalIgnoreCase)) return Error(400, "No puede eliminarse a sí mismo.");
            if (!admin.PuedeEditar(u)) return Error(403, SinPermisoJefe);
            if (u.EsAdmin && u.Activo && almacen.ContarAdminsActivos(u.Id) == 0) return Error(400, "Debe quedar al menos un administrador activo.");
            almacen.Eliminar(u.Login);
            almacen.Auditar(admin.Login, "USUARIO", null, null, null, null, $"Eliminó usuario {u.Login}", Ip(ctx));
            return Results.Ok(new { ok = true });
        });

        // ---- acciones rápidas (app del teléfono): desbloquear y activar / desactivar sin enviar toda la ficha ----
        adminApi.MapPost("/usuarios/{login}/desbloquear", (string login, HttpContext ctx) =>
        {
            var r = ExigirGestor(ctx, out var admin);
            if (r != null) return r;
            var u = almacen.ObtenerUsuario(login);
            if (u == null) return Error(404, $"No existe el usuario '{login}'.");
            if (!admin.PuedeEditar(u)) return Error(403, SinPermisoJefe);
            almacen.Desbloquear(u.Id);
            almacen.Auditar(admin.Login, "USUARIO", null, null, null, null, $"Desbloqueó al usuario {u.Login}", Ip(ctx));
            return Results.Ok(new { usuario = VistaUsuario(almacen.ObtenerUsuario(u.Login)!, admin) });
        });

        adminApi.MapPost("/usuarios/{login}/activo", (string login, ActivoPeticion p, HttpContext ctx) =>
        {
            var r = ExigirGestor(ctx, out var admin);
            if (r != null) return r;
            var u = almacen.ObtenerUsuario(login);
            if (u == null) return Error(404, $"No existe el usuario '{login}'.");
            if (!admin.PuedeEditar(u)) return Error(403, SinPermisoJefe);
            if (!p.Activo && u.Login.Equals(admin.Login, StringComparison.OrdinalIgnoreCase)) return Error(400, "No puede desactivarse a sí mismo.");
            if (!p.Activo && u.EsAdmin && u.Activo && almacen.ContarAdminsActivos(u.Id) == 0) return Error(400, "Debe quedar al menos un administrador activo.");
            almacen.Activar(u.Login, p.Activo);
            almacen.Auditar(admin.Login, "USUARIO", null, null, null, null, $"{(p.Activo ? "Activó" : "Desactivó")} al usuario {u.Login}", Ip(ctx));
            return Results.Ok(new { usuario = VistaUsuario(almacen.ObtenerUsuario(u.Login)!, admin) });
        });

    }

    private void MapearAuditoria()
    {
        // ------------------------------------------------------------------ auditoría (solo ADMIN)
        adminApi.MapGet("/auditoria", (HttpContext ctx, DateTime? desde, DateTime? hasta, string? usuario, string? tienda,
                                       string? tipo, string? texto, int? limite) =>
        {
            var r = ExigirGestor(ctx, out var quien);
            if (r != null) return r;
            if (!quien.EsAdmin) return Error(403, "Solo el administrador puede ver la auditoría.");
            var d = desde ?? DateTime.Today.AddDays(-7);
            var h = hasta ?? DateTime.Today;
            if (h < d) return Error(400, "La fecha Desde no puede ser posterior a Hasta.");
            var (filas, total) = almacen.BuscarAuditoria(new FiltroAuditoria(d, h, usuario, tienda, tipo, texto, limite ?? 5000,
                                                                               IncluirTiendas: quien.EsAdmin));   // el Jefe no ve cambios de conexiones
            return Results.Ok(new { total, filas });
        });

    }
}
