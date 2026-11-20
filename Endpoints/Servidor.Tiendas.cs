// Generado al dividir Program.cs por módulos: el código de cada parte es el mismo que estaba en Program.cs.
namespace VentasReportesApi;

public sealed partial class Servidor
{
    private void MapearTiendas()
    {
        adminApi.MapGet("/tiendas", (HttpContext ctx) =>
        {
            var r = ExigirAdmin(ctx, out _);
            if (r != null) return r;
            var usos = almacen.UsuariosPorTienda();
            return Results.Ok(new { tiendas = almacen.ListarTiendas().Select(t => VistaTienda(t, usos)).ToList() });
        });

        adminApi.MapPost("/tiendas", (TiendaPeticion p, HttpContext ctx) =>
        {
            var r = ExigirAdmin(ctx, out var admin);
            if (r != null) return r;
            var err = ValidarTienda(p, true);
            if (err != null) return Error(400, err);
            var t = DesdePeticion(p);
            if (almacen.ObtenerTienda(t.Nombre) != null) return Error(409, $"Ya existe la tienda '{t.Nombre}'.");
            almacen.CrearTienda(t, cifrado.Cifrar(p.Clave!));
            almacen.Auditar(admin.Login, "TIENDA", t.Nombre, null, null, null, $"Creó tienda {t.Nombre} ({t.Servidor}{(t.Puerto is > 0 ? "," + t.Puerto : "")}/{t.BaseDatos})", Ip(ctx));
            return Results.Ok(new { tienda = VistaTienda(almacen.ObtenerTienda(t.Nombre)!, almacen.UsuariosPorTienda()) });
        });

        adminApi.MapPut("/tiendas/{nombre}", (string nombre, TiendaPeticion p, HttpContext ctx) =>
        {
            var r = ExigirAdmin(ctx, out var admin);
            if (r != null) return r;
            var actual = almacen.ObtenerTienda(nombre);
            if (actual == null) return Error(404, $"No existe la tienda '{nombre}'.");
            var err = ValidarTienda(p, false);
            if (err != null) return Error(400, err);
            var t = DesdePeticion(p);
            if (!t.Nombre.Equals(actual.Nombre, StringComparison.CurrentCultureIgnoreCase) && almacen.ObtenerTienda(t.Nombre) != null)
                return Error(409, $"Ya existe otra tienda llamada '{t.Nombre}'.");
            almacen.ActualizarTienda(actual.Nombre, t, string.IsNullOrEmpty(p.Clave) ? null : cifrado.Cifrar(p.Clave));
            almacen.Auditar(admin.Login, "TIENDA", t.Nombre, null, null, null,
                $"Editó tienda {actual.Nombre}{(actual.Nombre != t.Nombre ? " → " + t.Nombre : "")} ({t.Servidor}{(t.Puerto is > 0 ? "," + t.Puerto : "")}/{t.BaseDatos}, {(t.Activa ? "activa" : "inactiva")}{(string.IsNullOrEmpty(p.Clave) ? "" : ", clave nueva")})", Ip(ctx));
            return Results.Ok(new { tienda = VistaTienda(almacen.ObtenerTienda(t.Nombre)!, almacen.UsuariosPorTienda()) });
        });

        adminApi.MapDelete("/tiendas/{nombre}", (string nombre, HttpContext ctx) =>
        {
            var r = ExigirAdmin(ctx, out var admin);
            if (r != null) return r;
            var t = almacen.ObtenerTienda(nombre);
            if (t == null) return Error(404, $"No existe la tienda '{nombre}'.");
            almacen.EliminarTienda(t.Nombre);
            almacen.Auditar(admin.Login, "TIENDA", t.Nombre, null, null, null, $"Eliminó tienda {t.Nombre}", Ip(ctx));
            return Results.Ok(new { ok = true });
        });

        // Importar tiendas desde un .config (<connectionStrings>). aplicar=false solo muestra lo que cambiaría.
        adminApi.MapPost("/tiendas/importar", (ImportarPeticion p, HttpContext ctx) =>
        {
            var r = ExigirAdmin(ctx, out var admin);
            if (r != null) return r;
            if (string.IsNullOrWhiteSpace(p.Contenido)) return Error(400, "El archivo está vacío.");
            if (p.Contenido.Length > 2_000_000) return Error(400, "El archivo es demasiado grande.");
            var res = ImportadorConfig.Importar(p.Contenido, almacen, cifrado, p.Aplicar);
            if (p.Aplicar)
            {
                almacen.Auditar(admin.Login, "TIENDA", null, null, null, null,
                    $"Importó tiendas desde archivo: {res.Nuevas.Count} nuevas ({string.Join(",", res.Nuevas)}), {res.Actualizadas.Count} actualizadas", Ip(ctx));
                cacheEstado.Clear();
            }
            return Results.Ok(new
            {
                nuevas = res.Nuevas, actualizadas = res.Actualizadas, sinCambios = res.SinCambios,
                noIncluidas = res.NoIncluidas, errores = res.Errores, avisos = res.Avisos, aplicado = p.Aplicar
            });
        });

        // Ver la clave SQL guardada de una tienda (exportación de conexiones a Excel: botón «Ver clave»).
        // Solo administradores, confirmando con su propia clave; cada consulta queda en la auditoría. La clave nunca se guarda en el Excel.
        adminApi.MapPost("/tiendas/{nombre}/ver-clave", async (string nombre, VerClavePeticion p, HttpContext ctx) =>
        {
            var r = ExigirAdmin(ctx, out var admin);
            if (r != null) return r;
            if (admin.BloqueadoHasta > DateTime.Now)
                return Error(429, $"Cuenta bloqueada por intentos fallidos. Intente después de las {admin.BloqueadoHasta:HH:mm}.");
            var t = almacen.ObtenerTienda(nombre);
            if (t == null) return Error(404, $"No existe la tienda '{nombre}'.");
            if (string.IsNullOrEmpty(p.ClaveAdmin) || !Claves.Verificar(p.ClaveAdmin, admin.HashClave))
            {
                almacen.RegistrarFallo(admin.Id, intentos, minutosBloqueo);
                almacen.Auditar(admin.Login, "DENEGADO", t.Nombre, null, null, null, $"Clave incorrecta al pedir ver la clave SQL de {t.Nombre}", Ip(ctx));
                await Task.Delay(1500);                                   // frena los intentos repetidos
                return Error(403, "Su clave de administrador no es correcta.");
            }
            if (admin.Fallos > 0) almacen.Desbloquear(admin.Id);
            almacen.Auditar(admin.Login, "TIENDA", t.Nombre, null, null, null, $"Vio la clave SQL de la tienda {t.Nombre}", Ip(ctx));
            return Results.Ok(new { tienda = t.Nombre, usuario = t.Usuario, clave = cifrado.Descifrar(t.ClaveCifrada) });
        });

        adminApi.MapPost("/tiendas/probar", async (TiendaPeticion p, HttpContext ctx) =>
        {
            var r = ExigirAdmin(ctx, out _);
            if (r != null) return r;
            string? clave = p.Clave;
            if (string.IsNullOrEmpty(clave) && !string.IsNullOrWhiteSpace(p.NombreOriginal))
            {
                var guardada = almacen.ObtenerTienda(p.NombreOriginal);
                if (guardada != null) clave = cifrado.Descifrar(guardada.ClaveCifrada);
            }
            if (string.IsNullOrWhiteSpace(p.Servidor) || string.IsNullOrWhiteSpace(p.BaseDatos) ||
                string.IsNullOrWhiteSpace(p.Usuario) || string.IsNullOrEmpty(clave))
                return Error(400, "Complete servidor, base de datos, usuario y clave para probar.");
            int timeout = Math.Clamp(p.Timeout ?? 15, 5, 30);
            string cadena = Conexiones.Cadena(p.Servidor.Trim(), p.Puerto, p.BaseDatos.Trim(), p.Usuario.Trim(), clave, timeout, reintentos: 0);
            return Results.Ok(await Conexiones.ProbarAsync(cadena, ctx.RequestAborted));
        });

    }
}
