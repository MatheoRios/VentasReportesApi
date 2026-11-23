# API de Reportes en Ubuntu (192.168.20.25)

## 1. En tu PC Windows: generar el paquete
1. Detén la API local si está abierta.
2. En la carpeta `VentasReportesApi\scripts`, doble clic en **`publicar-linux.bat`**.
   Genera **`ventasreportesapi-linux.tar.gz`** con:
   - el programa para Linux (no necesita instalar .NET en el servidor),
   - tus datos actuales (usuarios, tiendas con claves cifradas, auditoría y llaves),
   - estos scripts.

## 2. Copiarlo al servidor
Desde una ventana de comandos de Windows (scp viene con Windows 10/11):
```
scp ventasreportesapi-linux.tar.gz administrador@192.168.20.25:~
```
(o con WinSCP / una memoria USB)

## 3. En el servidor Linux
```
mkdir -p ~/ventasapi && tar -xzf ~/ventasreportesapi-linux.tar.gz -C ~/ventasapi
cd ~/ventasapi
sudo bash instalar-api.sh
```
Al final debe decir: **API funcionando: http://192.168.20.25:5080/api/salud**

## 4. Comprobar desde una PC Windows
Abra en el navegador: http://192.168.20.25:5080/api/salud

## Uso diario
| Acción | Comando |
|---|---|
| Estado | `sudo systemctl status ventasreportesapi` |
| Registro en vivo | `sudo journalctl -u ventasreportesapi -f` |
| Reiniciar | `sudo systemctl restart ventasreportesapi` |
| Usuarios | `ventasapi usuarios` / `ventasapi ayuda` |
| Respaldo manual | `sudo respaldar-ventasapi` (diario automático a las 02:30, 30 días, en `/var/backups/ventasreportesapi`) |

## Actualizar la API
Repita los pasos 1 a 3. El instalador reemplaza el programa y **conserva los datos del servidor**.

## Notas
- Puerto **5080**. Si el firewall (ufw) está activo, el instalador lo abre.
- Los datos viven en `/opt/ventasreportesapi/datos` (solo los lee el usuario del servicio).
- Si una tienda con SQL Server antiguo da error de conexión, el ajuste `openssl-legacy.cnf` ya viene aplicado al servicio.
