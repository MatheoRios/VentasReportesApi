namespace VentasReportesApi;

/// <summary>
/// Comandos de administración. Se ejecutan en la carpeta de la API, por ejemplo:
///   VentasReportesApi usuario-crear jperez "Juan Pérez" TIENDA
///   VentasReportesApi usuario-tiendas jperez BOSQUE,CENTRO
/// </summary>
public static class Consola
{
    public static int Ejecutar(string[] a, Almacen db, IReadOnlyCollection<string> sucursalesConfig)
    {
        try
        {
            string cmd = a[0].ToLowerInvariant();
            switch (cmd)
            {
                case "usuario-crear":
                {
                    Requiere(a, 4, "usuario-crear <login> \"<Nombre>\" <ADMIN|JEFE|REGIONAL|TIENDA> [tiendas separadas por coma]");
                    string rol = a[3].ToUpperInvariant();
                    if (!Almacen.Roles.Contains(rol)) return Error("Rol inválido. Use ADMIN, JEFE, REGIONAL o TIENDA.");
                    if (db.ObtenerUsuario(a[1]) != null) return Error($"El usuario '{a[1]}' ya existe.");
                    string clave = PedirClave();
                    db.CrearUsuario(a[1], a[2], rol, clave);
                    Ok($"Usuario '{a[1]}' creado con rol {rol}.");
                    if (a.Length > 4) return Asignar(db, a[1], a[4], sucursalesConfig, true);
                    if (rol is not ("ADMIN" or "JEFE")) Aviso("Recuerde asignarle tiendas:  VentasReportesApi usuario-tiendas " + a[1] + " BOSQUE,CENTRO");
                    return 0;
                }
                case "usuario-clave":
                {
                    Requiere(a, 2, "usuario-clave <login>");
                    if (db.ObtenerUsuario(a[1]) == null) return Error($"No existe el usuario '{a[1]}'.");
                    db.CambiarClave(a[1], PedirClave());
                    db.QuitarHuellasDe(a[1]);
                    return Ok("Clave actualizada (y usuario desbloqueado). Sus huellas se borraron: debe activarlas de nuevo.");
                }
                case "usuario-tiendas":
                    Requiere(a, 3, "usuario-tiendas <login> <TIENDA1,TIENDA2,...>   (reemplaza las actuales)");
                    return Asignar(db, a[1], a[2], sucursalesConfig, true);
                case "usuario-agregar-tiendas":
                    Requiere(a, 3, "usuario-agregar-tiendas <login> <TIENDA1,TIENDA2,...>");
                    return Asignar(db, a[1], a[2], sucursalesConfig, false);
                case "usuario-quitar-tiendas":
                {
                    Requiere(a, 3, "usuario-quitar-tiendas <login> <TIENDA1,...>");
                    var u = db.ObtenerUsuario(a[1]);
                    if (u == null) return Error($"No existe el usuario '{a[1]}'.");
                    db.QuitarSucursales(u.Id, Separar(a[2]));
                    return Ok("Tiendas quitadas. Ahora tiene: " + string.Join(", ", db.SucursalesDe(u.Id)));
                }
                case "usuario-rol":
                {
                    Requiere(a, 3, "usuario-rol <login> <ADMIN|JEFE|REGIONAL|TIENDA>");
                    string rol = a[2].ToUpperInvariant();
                    if (!Almacen.Roles.Contains(rol)) return Error("Rol inválido.");
                    return db.CambiarRol(a[1], rol) ? Ok("Rol actualizado.") : Error("No existe el usuario.");
                }
                case "usuario-desactivar":
                    Requiere(a, 2, "usuario-desactivar <login>");
                    return db.Activar(a[1], false) ? Ok("Usuario desactivado: pierde el acceso de inmediato.") : Error("No existe el usuario.");
                case "usuario-activar":
                    Requiere(a, 2, "usuario-activar <login>");
                    return db.Activar(a[1], true) ? Ok("Usuario activado y desbloqueado.") : Error("No existe el usuario.");
                case "usuario-eliminar":
                    Requiere(a, 2, "usuario-eliminar <login>");
                    return db.Eliminar(a[1]) ? Ok("Usuario eliminado.") : Error("No existe el usuario.");
                case "usuarios":
                {
                    var lista = db.ListarUsuarios();
                    if (lista.Count == 0) { Aviso("No hay usuarios todavía."); return 0; }
                    Console.WriteLine($"{"LOGIN",-16}{"NOMBRE",-26}{"ROL",-10}{"ESTADO",-12}{"ÚLTIMO ACCESO",-18}TIENDAS");
                    foreach (var u in lista)
                    {
                        string estado = !u.Activo ? "inactivo" : u.BloqueadoHasta > DateTime.Now ? "bloqueado" : "activo";
                        string tiendas = u.VeTodas ? "(todas)" : string.Join(", ", db.SucursalesDe(u.Id));
                        Console.WriteLine($"{Corta(u.Login, 15),-16}{Corta(u.Nombre, 25),-26}{u.Rol,-10}{estado,-12}{u.UltimoAcceso?.ToString("dd/MM/yy HH:mm") ?? "-",-18}{tiendas}");
                    }
                    return 0;
                }
                case "sucursales":
                    Console.WriteLine($"Tiendas configuradas ({sucursalesConfig.Count}):");
                    foreach (var s in sucursalesConfig) Console.WriteLine("  " + s);
                    return 0;
                case "auditoria":
                {
                    int n = a.Length > 1 && int.TryParse(a[1], out var x) ? x : 30;
                    foreach (var f in db.UltimaAuditoria(n))
                        Console.WriteLine($"{f[0]}  {f[1],-14} {f[2],-9} {f[3],-15} {f[4],-24} {f[5],8}  {f[6]}  {f[7]}");
                    return 0;
                }
                default:
                    Ayuda();
                    return cmd is "ayuda" or "help" or "/?" ? 0 : 1;
            }
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    private static int Asignar(Almacen db, string login, string lista, IReadOnlyCollection<string> config, bool reemplazar)
    {
        var u = db.ObtenerUsuario(login);
        if (u == null) return Error($"No existe el usuario '{login}'.");
        var pedidas = Separar(lista);
        var validas = new List<string>();
        foreach (var p in pedidas)
        {
            var real = config.FirstOrDefault(s => s.Equals(p, StringComparison.CurrentCultureIgnoreCase));
            if (real == null) Aviso($"'{p}' no existe como tienda: se omite.");
            else validas.Add(real);
        }
        db.AsignarSucursales(u.Id, validas, reemplazar);
        if (u.VeTodas) Aviso("Este usuario es ADMIN/JEFE: igual ve todas las tiendas.");
        return Ok($"{u.Login} ahora tiene: " + string.Join(", ", db.SucursalesDe(u.Id)));
    }

    private static List<string> Separar(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void Requiere(string[] a, int n, string uso)
    {
        if (a.Length < n) throw new ArgumentException("Uso: VentasReportesApi " + uso);
    }

    private static string PedirClave()
    {
        while (true)
        {
            string c1 = LeerOculto("Nueva clave (mín. 8 caracteres): ");
            var problema = Claves.ValidarPolitica(c1);
            if (problema != null) { Aviso(problema); continue; }
            string c2 = LeerOculto("Repita la clave: ");
            if (c1 == c2) return c1;
            Aviso("Las claves no coinciden.");
        }
    }

    private static string LeerOculto(string titulo)
    {
        Console.Write(titulo);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); } }
            else if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static string Corta(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;

    private static int Ok(string m) { Color(ConsoleColor.Green, "✔ " + m); return 0; }
    private static int Error(string m) { Color(ConsoleColor.Red, "✖ " + m); return 1; }
    private static void Aviso(string m) => Color(ConsoleColor.Yellow, "! " + m);
    private static void Color(ConsoleColor c, string m)
    {
        var antes = Console.ForegroundColor;
        Console.ForegroundColor = c;
        Console.WriteLine(m);
        Console.ForegroundColor = antes;
    }

    public static void Ayuda()
    {
        Console.WriteLine(@"
API de Reportes de Ventas - administración
==========================================
  VentasReportesApi                      Inicia la API (o use iniciar-api.bat)

Usuarios
  usuario-crear <login> ""<Nombre>"" <ADMIN|JEFE|REGIONAL|TIENDA> [TIENDA1,TIENDA2]
  usuario-clave <login>                  Cambia la clave (y desbloquea)
  usuario-rol <login> <ROL>
  usuario-tiendas <login> <T1,T2,...>    Reemplaza sus tiendas
  usuario-agregar-tiendas <login> <T1,...>
  usuario-quitar-tiendas <login> <T1,...>
  usuario-desactivar <login>             Pierde el acceso de inmediato
  usuario-activar <login>
  usuario-eliminar <login>
  usuarios                               Lista usuarios, estado y tiendas

Consultas
  sucursales                             Tiendas configuradas (se administran en Excel)
  importar-config <archivo.config>       Crea/actualiza tiendas desde un .config (<connectionStrings>)
  auditoria [n]                          Últimos n movimientos (por defecto 30)

Roles: ADMIN todo (incluye conexiones) · JEFE ve todas las tiendas y gestiona usuarios
       Tienda/Regional, sin conexiones · REGIONAL y TIENDA ven solo las asignadas.
");
    }
}
