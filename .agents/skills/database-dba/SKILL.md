---
name: database-dba
description: >-
  Motores de base de datos (SQLite WAL para seguridad/chat/auditoría y SQL Server
  KLKPOS_TIENDA + Stellar VAD10), migraciones idempotentes, reglas de SQL seguro,
  cultura invariante y contrato de cambios. Úsala al tocar SQL, esquemas o consultas.
---

# Base de Datos y DBA — SQLite + SQL Server (KLK / Stellar)

## 1. Motores y responsabilidades
1. **SQLite (`datos/seguridad.db`, modo WAL)** — gestionado en `VentasReportesApi/Datos/`:
   - Tablas: `Usuarios`, `UsuarioSucursal`, `Tiendas` (clave SQL cifrada con AES-GCM usando `datos/tiendas.key`), `Auditoria`, `Migraciones`, `Huellas`, `MonitorEstado`, `ChatMensajes`, `ChatLeidos`, `ChatPrivados`, `ChatArchivos`.
   - Toda conexión abre con `PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;`.
2. **SQL Server de tiendas (`KLKPOS_TIENDA` / `KLKPOS`, ~25 sucursales)**:
   - Tablas principales: `KLK_FACTURAHDR`, `KLK_FACTURALINE`, `KLK_NCREDITOHDR`, `KLK_NCREDITOLINE`, `KLK_CIERRE`, `KLK_CIERRE_X`, `KLK_CIERRE_DENOMINACION`, `KLK_CAJA`, `KLK_ENTRADAHDR`, `KLK_SAP_ARTICULO`, `KLK_SAP_EXISTENCIA`.
   - Procedimiento estándar de ventas: `dbo.getVentasPosStore`.
3. **SQL Server central de compras (`Stellar / VAD10`)**:
   - Conexión solo por configuración (`Reportes:ConexionCompras`). Tablas: `MA_COMPRAS`, `TR_INVENTARIO`, `MA_PRODUCTOS`, `MA_PROVEEDORES`, `MA_MONEDAS`, `MA_DEPARTAMENTOS`, `MA_ODC`, `TR_ODC`.

## 2. Reglas de migraciones en SQLite (`Datos/Auditoria.cs`)
- Toda migración en `Almacen.Migrar()` debe ser **100 % idempotente**:
  - Verificar columnas nuevas con `PRAGMA table_info(Tabla)` antes de ejecutar `ALTER TABLE`.
  - Registrar migraciones de datos únicas en la tabla `Migraciones` usando `INSERT OR IGNORE ...; SELECT changes();`.
- Nunca hacer cambios destructivos de columnas o tablas que rompan bases existentes en producción Linux (`/opt/ventasreportesapi/datos/seguridad.db`).

## 3. Reglas obligatorias para consultas SQL Server (`Consultas/Consulta*.cs`)
1. **Sin bloquear cajas POS**: anteponer siempre `Lectura.Prefijo` (`SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;`).
2. **Cultura invariante (CRÍTICO en `es-VE`)**:
   - **Nunca** interpolar valores `decimal` o `double` de C# directamente dentro de un string SQL (`$"{15.5m}"` produce `15,5` en `es-VE` y causa `Incorrect syntax near ','`).
   - Usar literales de texto con punto (`"15.5"`), `CultureInfo.InvariantCulture` o parámetros `@p`.
   - Toda consulta con SQL dinámico debe tener prueba unitaria ejecutando bajo `new CultureInfo("es-VE")` en `VentasReportesApi.Tests`.
3. **Parametrización y rangos de fecha sargables**:
   - Pasar fechas y filtros siempre por `SqlParameter` (`@p1`, `@p2`, etc.), nunca concatenando texto del usuario.
   - Filtrar fechas con rango semiabierto `[desde, DATEADD(DAY, 1, hasta))` para aprovechar índices (evitar `CAST(Fecha AS DATE) = ...` en el `WHERE`).
4. **Esquemas heterogéneos entre tiendas**:
   - Usar `ConsultaCierres.ColumnasAsync(conn, cadena, tabla, ...)` para detectar columnas opcionales.
   - La clave de caché de columnas debe ser `conn.DataSource|conn.Database|tabla` (**jamás** la cadena de conexión con contraseña).
5. **Reglas fiscales (`ConsultaLibroIva.cs`)**:
   - Documentos fiscales válidos: `EnEspera = 0`, `NoFiscal NOT IN ('1','True','S')` y `ISNULL(NumTicketFiscal, '') <> ''`.
   - Si una tienda devuelve `0` filas en Libro de IVA pero tiene facturas con `NoFiscal = 1` + `NumTicketFiscal` (caso detectado en Maracay `172.25.20.10`), verificar configuración KLK antes de alterar el filtro fiscal global.

## 4. Contrato de cambios de esquema / consultas
Antes de modificar una consulta en `Consultas/Consulta*.cs`:
1. Confirmar si cambia el orden o cantidad de columnas devueltas y actualizar simultáneamente `VentasExcelFunctions/Api/ApiCliente*.cs`.
2. Validar contra una tienda real con `VentasReportesPruebas` (`-r cruce` y `-r auditoria`).
