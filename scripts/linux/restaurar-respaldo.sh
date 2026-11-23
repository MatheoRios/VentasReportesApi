#!/usr/bin/env bash
# =============================================================================
#  Verificación (simulacro sin cambios) y restauración de respaldos cifrados
#  de VentasReportesApi (seguridad.db, tiendas.key, token.key, adjuntos, config)
#
#  Uso:
#    sudo bash restaurar-respaldo.sh --listar-drive
#    sudo bash restaurar-respaldo.sh --verificar <archivo.tar.gz.enc | --desde-drive ruta>
#    sudo bash restaurar-respaldo.sh --restaurar <archivo.tar.gz.enc | --desde-drive ruta>
# =============================================================================
set -euo pipefail

APP=ventasreportesapi
DIR_DATOS=/opt/$APP/datos
USR=ventasapi
ENV_FILE=/etc/$APP/respaldo-drive.env
RCLONE_REMOTE=gdrive-pos
RCLONE_CONFIG=/root/.config/rclone/rclone.conf
PASS_FILE=/etc/$APP/backup.pass

[ -f "$ENV_FILE" ] && source "$ENV_FILE"

verde()    { printf '\e[32m%s\e[0m\n' "$*"; }
amarillo() { printf '\e[33m%s\e[0m\n' "$*"; }
rojo()     { printf '\e[31m%s\e[0m\n' "$*"; }

uso() {
  cat <<USO
Uso:
  sudo bash restaurar-respaldo.sh --listar-drive
  sudo bash restaurar-respaldo.sh --verificar <ruta_local.tar.gz.enc>
  sudo bash restaurar-respaldo.sh --verificar --desde-drive <diarios/2026-11-23/archivo.tar.gz.enc>
  sudo bash restaurar-respaldo.sh --restaurar <ruta_local.tar.gz.enc>
  sudo bash restaurar-respaldo.sh --restaurar --desde-drive <diarios/2026-11-23/archivo.tar.gz.enc>
USO
}

[ "$(id -u)" -eq 0 ] || { rojo "Ejecute con sudo."; exit 1; }
[ $# -ge 1 ] || { uso; exit 1; }

MODO=""
DESDE_DRIVE=""
ARCHIVO=""

case "$1" in
  --listar-drive)
    MODO="listar"
    ;;
  --verificar)
    MODO="verificar"
    shift
    if [ "${1:-}" = "--desde-drive" ]; then
      DESDE_DRIVE="${2:-}"
    else
      ARCHIVO="${1:-}"
    fi
    ;;
  --restaurar)
    MODO="restaurar"
    shift
    if [ "${1:-}" = "--desde-drive" ]; then
      DESDE_DRIVE="${2:-}"
    else
      ARCHIVO="${1:-}"
    fi
    ;;
  *)
    uso
    exit 1
    ;;
esac

if [ "$MODO" = "listar" ]; then
  command -v rclone >/dev/null || { rojo "rclone no está instalado."; exit 1; }
  [ -f "$RCLONE_CONFIG" ] || { rojo "No existe $RCLONE_CONFIG. Ejecute configurar-respaldo-drive.sh primero."; exit 1; }
  echo "=== Respaldos diarios en Google Drive ($RCLONE_REMOTE:diarios) ==="
  rclone lsl "${RCLONE_REMOTE}:diarios" --config "$RCLONE_CONFIG" || true
  echo
  echo "=== Respaldos mensuales en Google Drive ($RCLONE_REMOTE:mensuales) ==="
  rclone lsl "${RCLONE_REMOTE}:mensuales" --config "$RCLONE_CONFIG" || true
  exit 0
fi

TMP=$(mktemp -d /tmp/restaurar-vpos.XXXXXX)
chmod 700 "$TMP"
trap 'rm -rf "$TMP"' EXIT

if [ -n "$DESDE_DRIVE" ]; then
  command -v rclone >/dev/null || { rojo "rclone no está instalado."; exit 1; }
  [ -f "$RCLONE_CONFIG" ] || { rojo "No existe $RCLONE_CONFIG."; exit 1; }
  echo "Descargando desde Google Drive ($RCLONE_REMOTE:$DESDE_DRIVE)..."
  rclone copy "${RCLONE_REMOTE}:$DESDE_DRIVE" "$TMP/in" --config "$RCLONE_CONFIG"
  rclone copy "${RCLONE_REMOTE}:${DESDE_DRIVE}.sha256" "$TMP/in" --config "$RCLONE_CONFIG" 2>/dev/null || true
  ARCHIVO=$(find "$TMP/in" -maxdepth 1 -type f -name '*.tar.gz.enc' | head -n 1)
fi

[ -n "$ARCHIVO" ] && [ -f "$ARCHIVO" ] || { rojo "No se encontró el archivo de respaldo cifrado."; exit 1; }

# Verificar SHA-256 si existe el archivo .sha256 junto al respaldo
if [ -f "${ARCHIVO}.sha256" ]; then
  DIR_ARCH="$(cd "$(dirname "$ARCHIVO")" && pwd)"
  NOM_ARCH="$(basename "$ARCHIVO")"
  (cd "$DIR_ARCH" && sha256sum -c "${NOM_ARCH}.sha256")
  verde "SHA-256 verificado."
else
  amarillo "Aviso: no se encontró ${ARCHIVO}.sha256; se continúa con descifrado."
fi

# Descifrar y verificar contenido + PRAGMA integrity_check antes de tocar producción
TAR_DEC="$TMP/respaldo.tar.gz"
PASS_ARG=()
TEMP_PASS=""
if [ -s "$PASS_FILE" ]; then
  PASS_ARG=(-pass "file:$PASS_FILE")
else
  read -r -s -p "Clave maestra de cifrado: " P_IN; echo
  TEMP_PASS="$TMP/.p"
  umask 077
  printf '%s' "$P_IN" > "$TEMP_PASS"
  unset P_IN
  PASS_ARG=(-pass "file:$TEMP_PASS")
fi

openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -in "$ARCHIVO" -out "$TAR_DEC" "${PASS_ARG[@]}"
[ -n "$TEMP_PASS" ] && rm -f "$TEMP_PASS"

tar -xzf "$TAR_DEC" -C "$TMP"
[ -f "$TMP/datos/seguridad.db" ] || { rojo "El archivo no contiene datos/seguridad.db."; exit 1; }

INTEG=$(sqlite3 "$TMP/datos/seguridad.db" "PRAGMA integrity_check;")
[ "$INTEG" = "ok" ] || { rojo "PRAGMA integrity_check falló: $INTEG"; exit 1; }

echo
verde "=== Verificación exitosa (simulación limpia, producción intacta) ==="
if [ -f "$TMP/manifest.txt" ]; then
  cat "$TMP/manifest.txt"
fi
echo "SQLite integrity_check : $INTEG"
echo "Usuarios en respaldo   : $(sqlite3 "$TMP/datos/seguridad.db" "SELECT COUNT(*) FROM Usuarios;")"
echo "Tiendas activas        : $(sqlite3 "$TMP/datos/seguridad.db" "SELECT COUNT(*) FROM Tiendas WHERE Activa = 1;")"

if [ "$MODO" = "verificar" ]; then
  exit 0
fi

# Modo --restaurar
echo
amarillo "ATENCIÓN: esto detendrá '$APP', reemplazará $DIR_DATOS con el respaldo verificado"
amarillo "          (creando primero un respaldo de seguridad previo en /var/backups/$APP)."
read -r -p "Escriba RESTAURAR para confirmar: " CONF
[ "$CONF" = "RESTAURAR" ] || { echo "Restauración cancelada."; exit 1; }

PRE="/var/backups/$APP/pre-restore-$(date +%F_%H%M%S)"
mkdir -p "$PRE"
chmod 700 "$PRE"
if [ -f "$DIR_DATOS/seguridad.db" ]; then
  sqlite3 "$DIR_DATOS/seguridad.db" ".backup '$PRE/seguridad.db'"
fi
for k in tiendas.key token.key; do
  [ -f "$DIR_DATOS/$k" ] && cp -f "$DIR_DATOS/$k" "$PRE/"
done
chmod 600 "$PRE"/* 2>/dev/null || true
verde "Respaldo previo de seguridad guardado en $PRE"

systemctl stop "$APP"
mkdir -p "$DIR_DATOS"
rm -f "$DIR_DATOS/seguridad.db-wal" "$DIR_DATOS/seguridad.db-shm"
cp -f "$TMP/datos/seguridad.db" "$DIR_DATOS/seguridad.db"
for k in tiendas.key token.key; do
  [ -f "$TMP/datos/$k" ] && cp -f "$TMP/datos/$k" "$DIR_DATOS/$k"
done
for sub in adjuntos complemento; do
  if [ -d "$TMP/datos/$sub" ]; then
    rm -rf "${DIR_DATOS:?}/$sub"
    cp -a "$TMP/datos/$sub" "$DIR_DATOS/$sub"
  fi
done
mkdir -p "$DIR_DATOS/adjuntos" "$DIR_DATOS/complemento"
chown -R "$USR:$USR" "$DIR_DATOS"
chmod 700 "$DIR_DATOS" "$DIR_DATOS/adjuntos" "$DIR_DATOS/complemento"
find "$DIR_DATOS" -type f -exec chmod 600 {} \;

systemctl start "$APP"
sleep 3
if curl -fs "http://127.0.0.1:5080/api/salud"; then
  echo
  verde "Restauración completada y API operativa."
else
  rojo "La API no respondió tras restaurar. Revise: sudo journalctl -u $APP -n 50 --no-pager"
  exit 1
fi
