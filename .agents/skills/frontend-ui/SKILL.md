---
name: frontend-ui
description: >-
  Stack de interfaces del ecosistema: complemento Excel WPF/WinForms + Ribbon
  (VentasExcelFunctions) y PWA móvil/web en vanilla JS + Tailwind CSS
  (VentasReportesApi/web-fuente -> wwwroot/app). Úsala al diseñar o modificar
  ventanas, cinta de Excel, hojas formateadas o pantallas de la app web.
---

# Frontend & UI — Excel WPF/Ribbon + PWA Web/Móvil

## 1. Stack de interfaces
El sistema tiene **dos clientes** que consumen la misma API:

### A. Complemento de Excel (`VentasExcelFunctions`, `.NET Framework 4.8`)
- **Cinta (Ribbon)**: `Infraestructura/RibbonMenu.cs` (`ExcelRibbon`, XML `customUI`, iconos dinámicos en `IconosCinta.cs`, visibilidad por permisos `Sesion.Tiene(...)` / `Sesion.TieneMarcado(...)`).
- **Ventanas WPF**: `UI/*.cs`, `Chat/*.cs`, `Herramientas/*.cs` (XAML inline cargado con `XamlReader.Parse(Tema.Xaml(Estilos.Con(Xaml)))`).
- **Temas**: 6 paletas + modo claro/oscuro en `Infraestructura/Tema.cs` y sincronización de libros en `Infraestructura/TemaExcel.cs`.
- **Hojas de Excel (COM/Interop)**: `Reportes/ExcelAyuda.cs` (`BloqueoExcel` y `ConfigurarImpresion`) y `Reportes/EjecutorReportes.cs` (`VentanaProgreso`, cancelación y omisión de tiendas lentas).

### B. App Web / PWA (`VentasReportesApi/web-fuente/` → `wwwroot/app/`)
- **Código fuente modular**: `web-fuente/js/00-base.js` hasta `99-inicio.js` + `web-fuente/input.css` + `web-fuente/tailwind.config.js`.
- **Compilación**: `cd web-fuente && node construir.js` concatena `js/*.js` en `wwwroot/app/app.js` y compila Tailwind en `wwwroot/app/app.css`.
- **Seguridad HTTP**: CSP estricta en `Program.cs` (`script-src 'self'`, sin scripts inline nuevos) y soporte WebAuthn (`70-ingreso-huella.js`).

## 2. Convenciones obligatorias de UI

### En Excel / WPF
1. **Nunca bloquear el hilo de Excel**:
   - Abrir formularios con `Ventanas.Aparte(...)` y ejecutar llamadas HTTP en segundo plano con `EjecutorReportes.EjecutarTrabajo(...)` o `Task.Run(...)`.
   - Toda escritura en Excel o invalidación de cinta desde otro hilo debe volver al hilo principal con `Ventanas.EnExcel(...)` o `ExcelAsyncUtil.QueueAsMacro(...)`.
2. **Protección COM/Interop**:
   - Envolver escritura masiva en `using (new BloqueoExcel(app))` para restaurar siempre `ScreenUpdating` y `DisplayAlerts`.
   - Escribir matrices `object[,]` en bloque (`rango.Value2 = datos`) en lugar de celda por celda.
3. **Permisos centralizados en `AdminVentana.cs`**:
   - Mantener una única definición (`permisosDef`) para todos los checkboxes de permisos; nunca duplicar listas manuales de controles en `Editable`, `RolCambiado`, `MarcarPermisos` o `PermisosMarcados`.
4. **Consistencia visual**:
   - Pasar todo XAML por `Tema.Xaml(Estilos.Con(Xaml))` para respetar el tema elegido y el modo oscuro.

### En la PWA (`web-fuente/` y `wwwroot/app/`)
1. **Editar siempre la fuente modular (`web-fuente/js/*.js`)** y reconstruir (o mantener sincronizado `wwwroot/app/app.js` si no se corre Node). Si cambia `app.js` o `app.css`, actualizar el parámetro `?v=` en `wwwroot/app/index.html` y `wwwroot/app/sw.js`.
2. **Escapar HTML siempre**: usar `esc(...)` al interpolar datos dinámicos en `innerHTML` para prevenir XSS.
3. **Sesión y cambio de clave**: al recibir un nuevo `token` / `expira` (p. ej. tras `/api/mi-clave`), actualizar `S.token`, `S.expira` y llamar a `guardarSesion()`.

## 3. Verificación
- Excel Add-In: compilar `VentasExcelFunctions.sln` (`0 errores`) y comprobar que genera `VentasExcelFunctions-AddIn64-packed.xll` y pasa `VentasExcelFunctions.Tests`.
- PWA: si se modificó `web-fuente/`, ejecutar `node construir.js` y verificar la carga en `http://localhost:5080/app/` sin errores de consola ni violaciones de CSP.
