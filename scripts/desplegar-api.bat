@echo off
setlocal
title Desplegar API de Reportes en el servidor
cd /d "%~dp0.."
set "SERVIDOR=arios@192.168.21.41"

echo.
echo  ============================================================
echo    Desplegar la API en %SERVIDOR%
echo    1) genera el paquete  2) lo copia  3) lo instala
echo  ============================================================
echo.
echo  [1/3] Generando el paquete...
call "%~dp0publicar-linux.bat" auto || goto error
for %%F in (ventasreportesapi-linux.tar.gz) do echo        Paquete: %%~tF  (%%~zF bytes)

echo.
echo  [2/3] Copiando al servidor (pide la clave de arios)...
scp ventasreportesapi-linux.tar.gz %SERVIDOR%:~ || goto error

echo.
echo  [3/3] Instalando en el servidor (pide la clave de arios y la de sudo)...
ssh -t %SERVIDOR% "rm -rf ~/api-paquete && mkdir ~/api-paquete && tar -xzf ~/ventasreportesapi-linux.tar.gz -C ~/api-paquete && cd ~/api-paquete && sudo bash instalar-api.sh && echo && echo VERSION INSTALADA: && curl -s http://localhost:5080/api/salud && echo" || goto error

echo.
echo  LISTO. Revise arriba la VERSION INSTALADA.
echo.
pause
exit /b 0

:error
echo.
echo  ERROR: el despliegue se detuvo. Revise el mensaje de arriba.
echo.
pause
exit /b 1
