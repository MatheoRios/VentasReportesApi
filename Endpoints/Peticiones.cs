// Cuerpos de las peticiones de la API (antes al final de Program.cs).
public sealed record LoginPeticion(string? Usuario, string? Clave);
public sealed record ReportePeticion(string Sucursal, DateTime Desde, DateTime Hasta);
/// <summary>Compras: período y localidades (códigos; vacío = todas las configuradas).</summary>
/// SoloConRecepcion (solo «Últimos costos»): solo compras que vinieron de una recepción («RECEPCION No …» en la observación).
public sealed record ComprasPeticion(DateTime Desde, DateTime Hasta, string[]? Localidades, bool SoloConRecepcion = false);
public sealed record CategoriasPeticion(string Sucursal, DateTime Desde, DateTime Hasta, string? Caja, string? NoFiscal, string? Credito);
public sealed record UsuarioPeticion(string? Login, string? Nombre, string? Rol, bool? Activo, string? Clave, List<string>? Tiendas, List<string>? Permisos);
public sealed record TiendaPeticion(string? NombreOriginal, string? Nombre, string? Servidor, int? Puerto, string? BaseDatos, string? Usuario, string? Clave, int? Timeout, bool? Activa);
public sealed record CambioClavePeticion(string? ClaveActual, string? ClaveNueva);
public sealed record ActivoPeticion(bool Activo);
/// <summary>Ver la clave SQL de una tienda: el administrador confirma con SU propia clave.</summary>
public sealed record VerClavePeticion(string? ClaveAdmin);
public sealed record ImportarPeticion(string? Contenido, bool Aplicar);
