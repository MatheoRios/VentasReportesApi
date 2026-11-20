---
name: csharp-backend
description: >-
  Convenciones C#/.NET, arquitectura enlazada de VentasReportesApi (.NET 8) y
  VentasExcelFunctions (.NET Framework 4.8 / Excel-DNA), seguridad, concurrencia
  y comandos de compilación y pruebas. Úsala al crear o modificar endpoints,
  clientes HTTP, servicios, seguridad o pruebas en C#.
---

# Backend C# / .NET — Reportes POS

## 1. Arquitectura de los proyectos enlazados
- **`VentasReportesApi`** (`ASP.NET Core .NET 8.0`, v1.31.0): API central, seguridad, caché, concurrencia y consultas SQL.
  - `Endpoints/`: `Servidor.cs`, `Servidor.Sesion.cs`, `Servidor.Reportes.cs`, `Servidor.Usuarios.cs`, `Servidor.Tiendas.cs`, `Peticiones.cs`.
  - `Datos/`: `Almacen.cs`, `Auditoria.cs`, `ChatDatos.cs`, `Tiendas.cs`, `ImportarConfig.cs`.
  - `Seguridad/`: `Seguridad.cs` (PBKDF2 + HMAC-SHA256 `Tokens`), `Huella.cs` (WebAuthn).
  - `Modulos/`: `Chat.cs`, `Complemento.cs`, `Concurrencia.cs`, `MonitorTiendas.cs`.
  - `Consultas/`: `ConsultaSucursal.cs` + 11 `Consulta*.cs`.
  - `Cli/`: `Consola.cs`.
  - `VentasReportesApi.Tests/`: pruebas unitarias MSTest (`.NET 8`).
- **`VentasExcelFunctions`** (`.NET Framework 4.8`, Excel-DNA 1.9): complemento de Excel.
  - `Api/`: `ApiCliente*.cs` y `ApiModelos.cs`.
  - `Reportes/`: `EjecutorReportes.cs`, `ExcelAyuda.cs` y generadores por reporte.
  - `Infraestructura/`: `RibbonMenu.cs`, `Actualizador.cs`, `Tema.cs`, `Ventanas.cs`, `ConexionesExcel.cs`.
  - `VentasExcelFunctions.Tests/`: pruebas unitarias MSTest.
- **`VentasReportesPruebas`** (`.NET 8`, carpeta hermana): ejecutor interactivo y parametrizado de reportes, cruces y auditoría de red.

## 2. Reglas obligatorias de backend y seguridad
1. **Autenticación y revocación**:
   - Todo endpoint autenticado valida `Autenticar(ctx)` (verifica `Activo == true` y `TokenVersion`) y exige `if (u.DebeCambiarClave) return Error(403, ClaveTemporal);` (excepto `/api/mi-clave` y `/api/logout`).
   - Cambiar/restablecer clave, desactivar usuario o llamar a `POST /api/logout` debe incrementar `TokenVersion`. `/api/mi-clave` devuelve un token renovado que tanto `ApiCliente.Tiendas.cs` como la PWA adoptan.
2. **Descifrado perezoso de tiendas**:
   - Nunca descifrar todas las tiendas en cada request. Usar `ValidarConsultaTienda(...)` + `CadenaObligatoria(permitida)` dentro del callback de `ConsultarTienda(...)` (solo en *cache miss*).
3. **Protección de los POS**:
   - Toda consulta a tienda pasa por `ConsultarTienda` (`Concurrencia` usuario → tienda → global + `Compartidos` deduplicación y caché acotada por filas).
4. **Cero secretos en repositorio o paquetes**:
   - Nunca incluir contraseñas SQL en código, tests ni en `dotnet publish`. `sucursales.json`, `tiendas-instancias.config` y `datos/` deben permanecer ignorados por `.gitignore` y excluidos en `VentasReportesApi.csproj`.
5. **Recursos en cliente C# (`VentasExcelFunctions`)**:
   - Encapsular siempre `CancellationTokenSource` en `using` (no filtrar timers).
   - Al cerrar sesión, capturar el token antes de `Sesion.Cerrar()` para enviarlo a `/api/logout`.
   - Mantener `Complemento.cs` con `UrlPublica`/`OrigenWeb` o validación estricta de `Host` para evitar *Host Header Injection*.

## 3. Contrato API ↔ Excel
- Muchos reportes devuelven filas como arrays posicionales (`object?[]`) parseados en `VentasExcelFunctions/Api/ApiCliente*.cs` por índice (`f[0]..f[n]`).
- **Regla de compatibilidad**: nunca reordenar ni eliminar columnas existentes en una respuesta; solo agregar columnas nuevas al final y leerlas de forma defensiva (`f.Count > idx`) en `ApiCliente*.cs`.

## 4. Verificación (`build` y `test`)
```powershell
# API y pruebas unitarias (.NET 8)
dotnet test "c:\app server\project C#\VentasReportesApi\VentasReportesApi.Tests\VentasReportesApi.Tests.csproj" -v minimal

# Complemento Excel (.NET Framework 4.8 + Excel-DNA packed .xll)
dotnet build "c:\app server\project C#\VentasExcelFunctions\VentasExcelFunctions.sln" --no-restore -m:1 -v minimal

# Pruebas operativas parametrizadas / auditoría de red
dotnet run --project "c:\app server\project C#\VentasReportesPruebas" -- -r cruce -t TURMERO --desde ayer
```
