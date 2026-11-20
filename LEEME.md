# API de Reportes de Ventas POS

Servicio central que **guarda las claves de las tiendas**, **valida usuarios** y **decide qué tiendas ve cada uno**.
El complemento de Excel ya no tiene contraseñas de SQL: solo la dirección de esta API.

```
Excel (complemento) ──HTTP──► API (esta carpeta) ──SQL──► tiendas
```

## Archivos

| Archivo | Para qué |
|---|---|
| `datos\seguridad.db` → tabla Tiendas | Conexiones a las tiendas, con la clave SQL **cifrada**. Se administran desde Excel: **Usuarios y accesos → Tiendas y conexiones**. |
| `datos\tiendas.key` | Llave que cifra las claves SQL (se crea sola). **Respáldela con la base**: sin ella no se pueden leer las claves. |
| `sucursales.json.importado` | Copia del formato anterior, importada automáticamente la primera vez. **Bórrela** cuando verifique que todo funciona (tiene claves en texto). |
| `appsettings.json` | Puerto, horas de sesión, bloqueo por intentos, consultas simultáneas. |
| `datos\seguridad.db` | Usuarios, tiendas asignadas y auditoría (se crea sola). **Respáldela.** |
| `datos\token.key` | Llave de sesiones (se crea sola). Si la borra, todos deben volver a ingresar. |

## 1. Compilar (una vez)

En Visual Studio: **Archivo → Abrir → Proyecto** → `VentasReportesApi.csproj` → **Compilar**.
O en una consola dentro de esta carpeta:

```
dotnet publish -c Release -o C:\VentasReportesApi
```

Requiere el **SDK de .NET 8** (viene con Visual Studio 2022/2026).

## 2. Crear el primer usuario (administrador)

Abra una consola en la carpeta publicada (o haga doble clic en `admin.bat`):

```
VentasReportesApi usuario-crear admin "Administrador" ADMIN
```

Le pedirá la clave dos veces (mínimo 8 caracteres).

## 3. Iniciar la API

Doble clic en **`iniciar-api.bat`**. Debe decir *"API de Reportes 1.0.0 lista. Tiendas configuradas: 24"*. La primera vez también avisa que importó las tiendas de `sucursales.json`.
Prueba rápida en el navegador: <http://localhost:5080/api/salud>

## 4. Crear usuarios y asignar tiendas

```
VentasReportesApi usuario-crear jperez "Juan Pérez" TIENDA BOSQUE
VentasReportesApi usuario-crear mgomez "María Gómez" REGIONAL BOSQUE,CENTRO,GUACARA
VentasReportesApi usuario-agregar-tiendas mgomez NAGUANAGUA
VentasReportesApi usuario-quitar-tiendas mgomez CENTRO
VentasReportesApi usuario-desactivar jperez        (pierde el acceso al instante)
VentasReportesApi usuarios                         (lista todo)
VentasReportesApi auditoria 50                     (quién consultó qué)
VentasReportesApi ayuda
```

- **ADMIN**: ve todas las tiendas.
- **REGIONAL / TIENDA**: ven solo las asignadas.
- 5 claves erradas seguidas → bloqueo de 15 minutos (`usuario-clave` o `usuario-activar` lo desbloquea).

## 5. Pasar a un servidor (cuando terminen las pruebas)

1. Copie la carpeta publicada al servidor (incluida `datos\` si quiere conservar los usuarios).
2. En `appsettings.json` cambie `"Urls": "http://0.0.0.0:5080"` para aceptar conexiones de la red.
3. Instálela como servicio de Windows (consola como administrador):
   ```
   sc create VentasReportesApi binPath= "C:\VentasReportesApi\VentasReportesApi.exe" start= auto
   sc start VentasReportesApi
   ```
4. Abra el puerto 5080 en el firewall del servidor.
5. En el `.xll.config` de cada PC cambie `ApiUrl` a `http://<servidor>:5080`.
6. **Recomendado en producción:** usar HTTPS con un certificado, para que las claves de usuario viajen cifradas.

## Seguridad

- Las claves de usuario se guardan con PBKDF2 (no se pueden leer).
- Las conexiones a las tiendas están en la base con la clave SQL cifrada (AES-GCM); restrinja la carpeta `datos` a administradores.
- En Excel, la pestaña **Tiendas y conexiones** permite crear, editar, desactivar y **probar** cada conexión; las claves nunca se muestran.
- Cada consulta queda en la auditoría con usuario, tienda, fechas, filas e IP.
