#!/usr/bin/env bash
# =============================================================================
#  HTTPS + acceso desde fuera de la oficina para la app de Reportes POS (Tailscale)
#  Uso, en el servidor:   sudo bash activar-https-tailscale.sh
#
#  Qué hace:
#   1. Instala Tailscale si falta (instalador oficial de tailscale.com).
#   2. Conecta este servidor a su red Tailscale (la primera vez muestra un enlace para iniciar sesión).
#   3. Publica la API por HTTPS SOLO dentro de su red Tailscale (tailscale serve):
#        https://<nombre-del-servidor>.<su-red>.ts.net  →  http://127.0.0.1:5080
#      El certificado lo emite y renueva Tailscale. No se abre ningún puerto a internet.
#   4. Le dice a la API cuál es su dirección HTTPS (necesario para el ingreso con huella / Face ID).
#      Se guarda aparte del programa, así que los despliegues siguientes no lo borran.
#
#  Se puede ejecutar varias veces: no duplica nada. La red local (http://IP:5080) sigue funcionando igual.
#  Para deshacerlo:  sudo tailscale serve reset  y  sudo rm /etc/systemd/system/ventasreportesapi.service.d/https.conf
# =============================================================================
set -euo pipefail

SERVICIO=ventasreportesapi
PUERTO=5080
NOMBRE_EQUIPO="${1:-reportes-pos}"

verde() { printf '\e[32m%s\e[0m\n' "$*"; }
amarillo() { printf '\e[33m%s\e[0m\n' "$*"; }
rojo() { printf '\e[31m%s\e[0m\n' "$*"; }

[ "$(id -u)" -eq 0 ] || { rojo "Ejecute con sudo:  sudo bash activar-https-tailscale.sh"; exit 1; }
systemctl cat "$SERVICIO" >/dev/null 2>&1 ||   # (con pipefail, "list-unit-files | grep -q" fallaba a veces)
  { rojo "No está instalada la API ($SERVICIO). Instálela primero con instalar-api.sh"; exit 1; }

# 1. Tailscale
if ! command -v tailscale >/dev/null; then
  echo "Instalando Tailscale…"
  curl -fsSL https://tailscale.com/install.sh | sh
fi
systemctl enable --now tailscaled >/dev/null 2>&1 || true

# 2. Conectar el servidor a la red (si todavía no lo está)
if ! tailscale status >/dev/null 2>&1; then
  amarillo "Abra el enlace que aparece abajo e inicie sesión con la cuenta de la empresa:"
  tailscale up --hostname="$NOMBRE_EQUIPO"
fi

DNS=$(tailscale status --json | python3 -c 'import sys,json; print(json.load(sys.stdin)["Self"]["DNSName"].rstrip("."))')
[ -n "$DNS" ] || { rojo "No se pudo leer el nombre del servidor en Tailscale."; exit 1; }
ORIGEN="https://$DNS"

# 3. HTTPS dentro de la red Tailscale → API local
echo "Publicando la API en $ORIGEN …"
if ! tailscale serve --bg "$PUERTO"; then
  rojo "No se pudo activar HTTPS."
  amarillo "En https://login.tailscale.com/admin/dns active «MagicDNS» y «HTTPS Certificates», y ejecute este script otra vez."
  exit 1
fi
tailscale serve status || true

# 4. Dirección HTTPS para la API (ingreso con huella). Archivo aparte: los despliegues no lo borran.
mkdir -p "/etc/systemd/system/$SERVICIO.service.d"
cat > "/etc/systemd/system/$SERVICIO.service.d/https.conf" <<EOS
[Service]
Environment=Reportes__OrigenWeb=$ORIGEN
EOS
systemctl daemon-reload
systemctl restart "$SERVICIO"
sleep 3

if curl -fsS "http://127.0.0.1:$PUERTO/api/huella/estado" | grep -q '"disponible":true'; then
  verde "Listo."
else
  amarillo "La API respondió, pero la huella no quedó activa: revise que la versión instalada sea 1.22.0 o posterior."
fi
echo
verde "App en el teléfono (con Tailscale conectado):  $ORIGEN/app/"
echo "Red de la oficina (sin cambios):                 http://$(hostname -I | awk '{print $1}'):$PUERTO/app/"
