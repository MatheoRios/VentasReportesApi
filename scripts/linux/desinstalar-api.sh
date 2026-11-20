#!/usr/bin/env bash
# Quita el servicio. Los datos quedan en /opt/ventasreportesapi/datos y los respaldos en /var/backups.
set -e
[ "$(id -u)" -eq 0 ] || { echo "Ejecute con sudo"; exit 1; }
systemctl disable --now ventasreportesapi 2>/dev/null || true
rm -f /etc/systemd/system/ventasreportesapi.service /etc/cron.d/ventasreportesapi /usr/local/bin/ventasapi
systemctl daemon-reload
echo "Servicio eliminado. Datos conservados en /opt/ventasreportesapi/datos (bórrelos a mano si ya no los necesita)."
