#!/usr/bin/env bash
# =============================================================================
#  Conexión del reporte "Compras de productos" (base central Stellar, p. ej. VAD10)
#  Uso, en el servidor:   sudo bash configurar-compras.sh
#
#  Pide servidor, base, usuario y clave de SQL Server y los guarda SOLO en este servidor:
#    /etc/ventasreportesapi/compras.env   (dueño root, permisos 600: nadie más lo puede leer)
#  La API lo lee al arrancar (variable Reportes__ConexionCompras). La clave no queda en el código,
#  ni en el paquete de instalación, ni en el historial de la terminal.
#  Se puede ejecutar otra vez para cambiar la clave. Para quitarlo:
#    sudo rm /etc/ventasreportesapi/compras.env /etc/systemd/system/ventasreportesapi.service.d/compras.conf
# =============================================================================
set -euo pipefail
SERVICIO=ventasreportesapi
PUERTO=5080
DIR=/etc/ventasreportesapi
ENV="$DIR/compras.env"

verde() { printf '\e[32m%s\e[0m\n' "$*"; }
amarillo() { printf '\e[33m%s\e[0m\n' "$*"; }
rojo() { printf '\e[31m%s\e[0m\n' "$*"; }

[ "$(id -u)" -eq 0 ] || { rojo "Ejecute con sudo:  sudo bash configurar-compras.sh"; exit 1; }
systemctl cat "$SERVICIO" >/dev/null 2>&1 ||   # (con pipefail, "list-unit-files | grep -q" fallaba a veces)
  { rojo "No está instalada la API ($SERVICIO)."; exit 1; }

read -r -p "Servidor SQL [192.168.20.3]: " SRV;  SRV=${SRV:-192.168.20.3}
if [[ "$SRV" != *,* && "$SRV" != *\\* ]]; then
  read -r -p "Puerto de SQL Server [1433]: " PTO; PTO=${PTO:-1433}
  [[ "$PTO" =~ ^[0-9]+$ ]] || { rojo "El puerto debe ser un número."; exit 1; }
  [ "$PTO" = "1433" ] || SRV="$SRV,$PTO"          # SQL Server escribe el puerto así:  servidor,puerto
fi
read -r -p "Base de datos [VAD10]: " BD;        BD=${BD:-VAD10}
read -r -p "Usuario SQL [reportRetail]: " USR;  USR=${USR:-reportRetail}
read -r -s -p "Clave de $USR: " CLAVE; echo
[ -n "$CLAVE" ] || { rojo "La clave no puede estar vacía."; exit 1; }
case "$CLAVE" in *\;*|*\"*|*\'*) rojo "La clave tiene ; o comillas: cámbiela por una sin esos caracteres."; exit 1;; esac

HOST=${SRV%%,*}; HOST=${HOST%%\\*}; P=1433
[[ "$SRV" == *,* ]] && P=${SRV##*,}
echo "Probando conexión a $HOST:$P …"
if timeout 5 bash -c "</dev/tcp/$HOST/$P" 2>/dev/null; then verde "El servidor SQL responde."
else amarillo "No se pudo abrir $HOST:$P desde este servidor (firewall o ruta entre redes). Se guarda igual; revise la red."; fi

mkdir -p "$DIR"; chmod 700 "$DIR"
umask 077
cat > "$ENV" <<EOS
Reportes__ConexionCompras=Server=$SRV;Database=$BD;User Id=$USR;Password=$CLAVE;TrustServerCertificate=True;Encrypt=False;Application Name=ReportesPOS-Compras;Connect Timeout=15
EOS
chown root:root "$ENV"; chmod 600 "$ENV"
unset CLAVE

mkdir -p "/etc/systemd/system/$SERVICIO.service.d"
cat > "/etc/systemd/system/$SERVICIO.service.d/compras.conf" <<EOS
[Service]
EnvironmentFile=-$ENV
EOS
systemctl daemon-reload
systemctl restart "$SERVICIO"
sleep 3
if curl -fsS "http://127.0.0.1:$PUERTO/api/salud" >/dev/null; then
  verde "Listo. La API se reinició con la conexión de compras ($SRV / $BD, usuario $USR)."
  echo "Pruébelo en Excel con el botón «Compras» (necesita el permiso «compras» y la API 1.25.0 o posterior)."
else
  rojo "La API no respondió después de reiniciar: revise  sudo journalctl -u $SERVICIO -n 50"
fi
