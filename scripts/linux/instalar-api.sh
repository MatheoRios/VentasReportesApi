#!/usr/bin/env bash
# =============================================================================
#  Instala / actualiza la API de Reportes de Ventas POS en Linux (Mint / Ubuntu)
#  Uso:   sudo bash instalar-api.sh
#  - Volver a ejecutarlo actualiza el programa SIN tocar los datos del servidor.
# =============================================================================
set -euo pipefail

APP=ventasreportesapi
DIR=/opt/$APP
USR=ventasapi
PUERTO=5080
ORIGEN="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

verde() { printf '\e[32m%s\e[0m\n' "$*"; }
amarillo() { printf '\e[33m%s\e[0m\n' "$*"; }
rojo() { printf '\e[31m%s\e[0m\n' "$*"; }

[ "$(id -u)" -eq 0 ] || { rojo "Ejecute con sudo:  sudo bash instalar-api.sh"; exit 1; }
[ -x "$ORIGEN/app/VentasReportesApi" ] || [ -f "$ORIGEN/app/VentasReportesApi" ] || { rojo "No se encuentra $ORIGEN/app/VentasReportesApi. Descomprima el paquete completo."; exit 1; }

echo "=== API de Reportes de Ventas POS: instalación en $(hostname) ==="

# 1. Dependencias del sistema (ICU para idioma/fechas, sqlite3 para respaldos, curl para probar)
FALTAN=()
ldconfig -p | grep -q libicuuc || FALTAN+=(libicu-dev)
command -v sqlite3 >/dev/null || FALTAN+=(sqlite3)
command -v curl >/dev/null || FALTAN+=(curl)
if [ ${#FALTAN[@]} -gt 0 ]; then
  echo "Instalando: ${FALTAN[*]}"
  apt-get update -qq && apt-get install -y -qq "${FALTAN[@]}"
fi

# 2. Usuario del sistema (sin acceso de inicio de sesión)
id "$USR" >/dev/null 2>&1 || useradd --system --home-dir "$DIR" --shell /usr/sbin/nologin "$USR"

# 3. Copiar el programa (los datos existentes en el servidor NO se tocan)
systemctl stop "$APP" 2>/dev/null || true
mkdir -p "$DIR"
cp -a "$ORIGEN/app/." "$DIR/"
cp -f "$ORIGEN/openssl-legacy.cnf" "$DIR/openssl-legacy.cnf"
if [ ! -f "$DIR/datos/seguridad.db" ] && [ -d "$ORIGEN/datos" ]; then
  mkdir -p "$DIR/datos"
  cp -a "$ORIGEN/datos/." "$DIR/datos/"
  verde "Datos copiados (usuarios, tiendas, auditoría y llaves)."
elif [ -f "$DIR/datos/seguridad.db" ]; then
  echo "Se conservan los datos que ya tenía el servidor."
fi
mkdir -p "$DIR/datos"
rm -f "$DIR/sucursales.json" "$DIR"/datos/*.importado 2>/dev/null || true
chmod +x "$DIR/VentasReportesApi"
chown -R "$USR:$USR" "$DIR"
chmod 750 "$DIR"
chmod 700 "$DIR/datos"
find "$DIR/datos" -type f -exec chmod 600 {} \;

# 4. Comando de administración:  ventasapi usuarios | ventasapi usuario-crear ...
cat > /usr/local/bin/ventasapi <<EOS
#!/usr/bin/env bash
cd "$DIR" && exec sudo -u $USR env OPENSSL_CONF=$DIR/openssl-legacy.cnf "$DIR/VentasReportesApi" "\$@"
EOS
chmod 755 /usr/local/bin/ventasapi

# 5. Servicio systemd (arranca solo y se reinicia si falla)
cat > /etc/systemd/system/$APP.service <<EOS
[Unit]
Description=API de Reportes de Ventas POS
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$USR
Group=$USR
WorkingDirectory=$DIR
ExecStart=$DIR/VentasReportesApi
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production
# Permite conectar con SQL Server antiguos (2008/2012, TLS 1.0) solo para este programa
Environment=OPENSSL_CONF=$DIR/openssl-legacy.cnf
SyslogIdentifier=$APP
NoNewPrivileges=true
ProtectSystem=full
ProtectHome=true
PrivateTmp=true
ReadWritePaths=$DIR/datos

[Install]
WantedBy=multi-user.target
EOS
systemctl daemon-reload
systemctl enable --now "$APP" >/dev/null

# 6. Firewall
if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
  ufw allow "$PUERTO/tcp" >/dev/null && verde "Firewall: puerto $PUERTO abierto."
else
  amarillo "Firewall (ufw) no está activo: no hizo falta abrir el puerto."
fi

# 7. Respaldo diario 02:30 (30 días)
cat > /usr/local/bin/respaldar-ventasapi <<EOS
#!/usr/bin/env bash
set -e
DEST=/var/backups/$APP/\$(date +%F)
mkdir -p "\$DEST"
sqlite3 "$DIR/datos/seguridad.db" ".backup '\$DEST/seguridad.db'"
cp -f "$DIR/datos/tiendas.key" "$DIR/datos/token.key" "\$DEST/" 2>/dev/null || true
chmod -R 600 "\$DEST"/*; chmod 700 "\$DEST"
find /var/backups/$APP -mindepth 1 -maxdepth 1 -type d -mtime +30 -exec rm -rf {} +
EOS
chmod 750 /usr/local/bin/respaldar-ventasapi
echo "30 2 * * * root /usr/local/bin/respaldar-ventasapi" > /etc/cron.d/$APP
chmod 644 /etc/cron.d/$APP
/usr/local/bin/respaldar-ventasapi && verde "Respaldo inicial hecho en /var/backups/$APP"

# 8. Prueba
sleep 4
if curl -fs "http://localhost:$PUERTO/api/salud" >/dev/null; then
  IP=$(hostname -I | awk '{print $1}')
  verde "API funcionando:  http://$IP:$PUERTO/api/salud"
else
  rojo "La API no respondió. Revise:  journalctl -u $APP -n 50 --no-pager"
  exit 1
fi

echo
echo "Comandos útiles:"
echo "  sudo systemctl status $APP        estado del servicio"
echo "  sudo journalctl -u $APP -f        ver el registro en vivo"
echo "  sudo systemctl restart $APP       reiniciar"
echo "  ventasapi usuarios                listar usuarios"
echo "  ventasapi usuario-crear admin \"Administrador\" ADMIN    (si no copió los datos)"
echo "  ventasapi ayuda                   todos los comandos"
