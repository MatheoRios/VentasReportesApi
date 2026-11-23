@echo off
setlocal
title Publicar API de Reportes para Linux
cd /d "%~dp0.."
set "PAQ=%CD%\paquete-linux"
set "AUTO="
if /i "%~1"=="auto" set "AUTO=1"

echo.
echo  Genera ventasreportesapi-linux.tar.gz para instalar en el servidor Linux.
echo  Conviene detener la API local para copiar la base de datos completa.
echo.
if defined AUTO goto seguir
choice /C SN /M " Detener la API local ahora"
if errorlevel 2 goto seguir
taskkill /IM VentasReportesApi.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul
:seguir

if exist "%PAQ%" rmdir /s /q "%PAQ%"
echo.
echo  Compilando para Linux (puede tardar un minuto la primera vez)...
dotnet publish VentasReportesApi.csproj -c Release -r linux-x64 --self-contained true -o "%PAQ%\app" -v q || goto error

rem En el servidor la API escucha en toda la red (puerto 5080)
> "%PAQ%\app\appsettings.Production.json" echo {"Urls": "http://0.0.0.0:5080"}

rem Datos actuales: usuarios, tiendas (claves cifradas), auditoria y llaves
if exist "bin\Debug\net8.0\datos\seguridad.db" (
  xcopy /E /I /Y "bin\Debug\net8.0\datos" "%PAQ%\datos" >nul
  del /q "%PAQ%\datos\*.importado" 2>nul
)
copy /Y "scripts\linux\instalar-api.sh" "%PAQ%\" >nul
copy /Y "scripts\linux\desinstalar-api.sh" "%PAQ%\" >nul
copy /Y "scripts\linux\activar-https-tailscale.sh" "%PAQ%\" >nul
copy /Y "scripts\linux\configurar-compras.sh" "%PAQ%\" >nul
copy /Y "scripts\linux\configurar-respaldo-drive.sh" "%PAQ%\" >nul
copy /Y "scripts\linux\restaurar-respaldo.sh" "%PAQ%\" >nul
copy /Y "scripts\linux\openssl-legacy.cnf" "%PAQ%\" >nul
copy /Y "scripts\linux\LEEME-LINUX.md" "%PAQ%\" >nul

if exist "ventasreportesapi-linux.tar.gz" del /q "ventasreportesapi-linux.tar.gz"
tar -czf "ventasreportesapi-linux.tar.gz" -C "%PAQ%" . || goto error
if exist "%PAQ%" rmdir /s /q "%PAQ%"

echo.
echo  Listo: %CD%\ventasreportesapi-linux.tar.gz
echo.
echo  Siguiente paso (ver scripts\linux\LEEME-LINUX.md):
echo    scp ventasreportesapi-linux.tar.gz administrador@192.168.20.25:~
echo    y en el servidor:  sudo bash instalar-api.sh
echo.
if not defined AUTO pause
exit /b 0

:error
echo.
echo  ERROR al generar el paquete. Revise el mensaje de arriba.
if not defined AUTO pause
exit /b 1
