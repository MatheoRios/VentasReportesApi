#!/usr/bin/env bash
# =============================================================================
#  Configura respaldo automático cifrado (AES-256 + SQLite .backup + rclone)
#  hacia Google Drive (Cerebro Antigravity/POS_GranjaMercado/Respaldos_BD)
#
#  Uso en Ubuntu (192.168.20.25):
#    sudo bash configurar-respaldo-drive.sh [DRIVE_FOLDER_ID]
# =============================================================================
set -euo pipefail

APP=ventasreportesapi
DIR_API=/opt/$APP/datos
DIR_CFG=/etc/$APP
PASS_FILE="$DIR_CFG/backup.pass"
ENV_FILE="$DIR_CFG/respaldo-drive.env"
RCLONE_DIR=/root/.config/rclone
RCLONE_CONF="$RCLONE_DIR/rclone.conf"
REMOTE_NAME=gdrive-pos
DEFAULT_FOLDER_ID="1rmroY0BPpXDQqhcL2czGwUdTegXiuwbb"
DRIVE_FOLDER_ID="${1:-$DEFAULT_FOLDER_ID}"

verde()    { printf '\e[32m%s\e[0m\n' "$*"; }
amarillo() { printf '\e[33m%s\e[0m\n' "$*"; }
rojo()     { printf '\e[31m%s\e[0m\n' "$*"; }

[ "$(id -u)" -eq 0 ] || { rojo "Ejecute con sudo:  sudo bash configurar-respaldo-drive.sh"; exit 1; }
[ -f "$DIR_API/seguridad.db" ] || { rojo "No se encontró $DIR_API/seguridad.db. Instale primero la API."; exit 1; }

echo "=== Configurando respaldo cifrado 3-2-1 a Google Drive ($APP) ==="

# 1. Dependencias (sqlite3, openssl, rclone)
FALTAN=()
command -v sqlite3 >/dev/null || FALTAN+=(sqlite3)
command -v openssl >/dev/null || FALTAN+=(openssl)
command -v rclone  >/dev/null || FALTAN+=(rclone)
if [ ${#FALTAN[@]} -gt 0 ]; then
  echo "Instalando paquetes necesarios: ${FALTAN[*]}"
  apt-get update -qq && apt-get install -y -qq "${FALTAN[@]}"
fi

# 2. Carpeta destino en Google Drive (acotada por root_folder_id)
if [ -z "${1:-}" ]; then
  read -r -p "ID carpeta Respaldos_BD en Drive [$DEFAULT_FOLDER_ID]: " INPUT_ID
  DRIVE_FOLDER_ID="${INPUT_ID:-$DEFAULT_FOLDER_ID}"
fi
[ -n "$DRIVE_FOLDER_ID" ] || { rojo "El ID de carpeta en Drive no puede estar vacío."; exit 1; }

# 3. Frase secreta de cifrado AES-256 (NUNCA se sube a Drive)
mkdir -p "$DIR_CFG"
chmod 700 "$DIR_CFG"
if [ ! -f "$PASS_FILE" ]; then
  echo
  echo "Clave maestra de cifrado AES-256 (para proteger seguridad.db, tiendas.key y token.key en Drive):"
  echo "  1) Generar automáticamente una clave fuerte aleatoria (recomendado)"
  echo "  2) Escribir manualmente una frase secreta propia (mínimo 12 caracteres)"
  read -r -p "Elija opción [1]: " OP_PASS
  OP_PASS="${OP_PASS:-1}"
  umask 077
  if [ "$OP_PASS" = "2" ]; then
    read -r -s -p "Frase secreta (mín. 12 caracteres): " P1; echo
    [ ${#P1} -ge 12 ] || { rojo "La frase debe tener al menos 12 caracteres."; exit 1; }
    read -r -s -p "Repita la frase secreta: " P2; echo
    [ "$P1" = "$P2" ] || { rojo "Las frases no coinciden."; exit 1; }
    printf '%s' "$P1" > "$PASS_FILE"
    unset P1 P2
  else
    GEN_PASS="$(openssl rand -base64 24 | tr -d '\n')"
    printf '%s' "$GEN_PASS" > "$PASS_FILE"
    echo
    amarillo "=================================================================="
    amarillo "  GUARDE ESTA CLAVE FUERA DE GOOGLE DRIVE (gestor / bóveda):"
    echo     "  $GEN_PASS"
    amarillo "  Sin esta clave NO se pueden descifrar los respaldos en un servidor nuevo."
    amarillo "=================================================================="
    unset GEN_PASS
  fi
  chown root:root "$PASS_FILE"
  chmod 600 "$PASS_FILE"
  verde "Clave de cifrado guardada en $PASS_FILE (permisos 600)."
else
  chmod 600 "$PASS_FILE"
  echo "Se conserva la clave de cifrado existente en $PASS_FILE."
fi

# 4. Configurar remoto rclone acotado a Respaldos_BD
mkdir -p "$RCLONE_DIR"
chmod 700 "$RCLONE_DIR"
if ! rclone listremotes --config "$RCLONE_CONF" 2>/dev/null | grep -qx "${REMOTE_NAME}:"; then
  echo
  echo "Autorización de Google Drive para rclone (cuenta granjamercado.ia@gmail.com):"
  echo "  En tu PC Windows (con rclone instalado) o donde tengas navegador, ejecuta:"
  echo "    rclone authorize \"drive\""
  echo "  Luego pega aquí el bloque JSON del token (ej. {\"access_token\":...})."
  echo "  Si prefieres usar el asistente interactivo en este servidor, deja el campo vacío y pulsa Enter."
  read -r -p "Token JSON (o Enter para asistente rclone config): " TOKEN_JSON
  umask 077
  if [ -n "$TOKEN_JSON" ]; then
    rclone config create "$REMOTE_NAME" drive \
      scope drive \
      root_folder_id "$DRIVE_FOLDER_ID" \
      token "$TOKEN_JSON" \
      --non-interactive --config "$RCLONE_CONF" >/dev/null
  else
    amarillo "En el asistente: nombre=$REMOTE_NAME | storage=drive | scope=drive | root_folder_id=$DRIVE_FOLDER_ID"
    rclone config --config "$RCLONE_CONF"
  fi
  chown root:root "$RCLONE_CONF"
  chmod 600 "$RCLONE_CONF"
else
  chmod 600 "$RCLONE_CONF"
  rclone config update "$REMOTE_NAME" root_folder_id "$DRIVE_FOLDER_ID" --non-interactive --config "$RCLONE_CONF" >/dev/null || true
  echo "Remoto $REMOTE_NAME ya configurado en $RCLONE_CONF (root_folder_id=$DRIVE_FOLDER_ID)."
fi

echo "Verificando conexión con Google Drive ($REMOTE_NAME:)..."
rclone lsd "${REMOTE_NAME}:" --config "$RCLONE_CONF" >/dev/null
rclone mkdir "${REMOTE_NAME}:diarios"   --config "$RCLONE_CONF"
rclone mkdir "${REMOTE_NAME}:mensuales" --config "$RCLONE_CONF"
verde "Conexión con Respaldos_BD verificada (diarios/ y mensuales/ listos)."

# 5. Guardar configuración permanente (protege este script ante futuros deploys de instalar-api.sh)
umask 077
cat > "$ENV_FILE" <<EOS
RCLONE_REMOTE=$REMOTE_NAME
RCLONE_CONFIG=$RCLONE_CONF
DRIVE_FOLDER_ID=$DRIVE_FOLDER_ID
PASS_FILE=$PASS_FILE
RETENCION_LOCAL_DIAS=30
RETENCION_DRIVE_DIAS=30
RETENCION_DRIVE_MESES=12
EOS
chown root:root "$ENV_FILE"
chmod 600 "$ENV_FILE"

# 6. Script diario /usr/local/bin/respaldar-ventasapi (SQLite .backup + integridad + tar.gz + AES-256 + Drive)
cat > /usr/local/bin/respaldar-ventasapi <<'EOS'
#!/usr/bin/env bash
set -euo pipefail

ENV_FILE=/etc/ventasreportesapi/respaldo-drive.env
[ -f "$ENV_FILE" ] || { echo "ERROR: falta $ENV_FILE"; exit 1; }
# shellcheck source=/dev/null
source "$ENV_FILE"

APP=ventasreportesapi
DIR_DATOS=/opt/$APP/datos
BACKUP_ROOT=/var/backups/$APP
FECHA=$(date +%F)
DIA_MES=$(date +%d)
MES=$(date +%Y-%m)
TS=$(date +%F_%H%M%S)
HOST=$(hostname -s 2>/dev/null || echo server)
DEST_LOCAL="$BACKUP_ROOT/$FECHA"

log() {
  local msg="[$(date '+%F %T')] $*"
  echo "$msg"
  logger -t respaldar-ventasapi "$*" 2>/dev/null || true
}

[ -f "$DIR_DATOS/seguridad.db" ] || { log "ERROR: no existe $DIR_DATOS/seguridad.db"; exit 1; }
[ -s "$PASS_FILE" ] || { log "ERROR: falta clave maestra en $PASS_FILE"; exit 1; }

TMP=$(mktemp -d /tmp/respaldo-vpos.XXXXXX)
chmod 700 "$TMP"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$TMP/datos" "$TMP/config"

# 1. Copia atómica consistente en caliente (segura con SQLite WAL activo)
sqlite3 "$DIR_DATOS/seguridad.db" ".backup '$TMP/datos/seguridad.db'"
INTEG=$(sqlite3 "$TMP/datos/seguridad.db" "PRAGMA integrity_check;")
if [ "$INTEG" != "ok" ]; then
  log "ERROR CRÍTICO: PRAGMA integrity_check devolvió '$INTEG'. Respaldo abortado."
  exit 1
fi

# 2. Llaves y carpetas operativas (adjuntos / complemento)
for k in tiendas.key token.key; do
  [ -f "$DIR_DATOS/$k" ] && cp -f "$DIR_DATOS/$k" "$TMP/datos/"
done
for sub in adjuntos complemento; do
  if [ -d "$DIR_DATOS/$sub" ] && [ -n "$(ls -A "$DIR_DATOS/$sub" 2>/dev/null)" ]; then
    cp -a "$DIR_DATOS/$sub" "$TMP/datos/"
  fi
done

# 3. Configuración del servidor (NUNCA se incluye backup.pass, respaldo-drive.env ni rclone.conf)
[ -f /etc/ventasreportesapi/compras.env ] && cp -f /etc/ventasreportesapi/compras.env "$TMP/config/"
[ -d /etc/systemd/system/ventasreportesapi.service.d ] && cp -a /etc/systemd/system/ventasreportesapi.service.d "$TMP/config/"
[ -f /etc/netplan/60-rutas-tiendas-pos.yaml ] && cp -f /etc/netplan/60-rutas-tiendas-pos.yaml "$TMP/config/"
[ -f /etc/docker/daemon.json ] && cp -f /etc/docker/daemon.json "$TMP/config/"

CANT_USUARIOS=$(sqlite3 "$TMP/datos/seguridad.db" "SELECT COUNT(*) FROM Usuarios;" 2>/dev/null || echo "?")
CANT_TIENDAS=$(sqlite3 "$TMP/datos/seguridad.db" "SELECT COUNT(*) FROM Tiendas WHERE Activa = 1;" 2>/dev/null || echo "?")
cat > "$TMP/manifest.txt" <<MAN
fecha_vet=$(date -Iseconds)
host=$HOST
sqlite_integrity=$INTEG
usuarios=$CANT_USUARIOS
tiendas_activas=$CANT_TIENDAS
cifrado=AES-256-CBC-PBKDF2-200000
MAN

# 4. Empaquetar y cifrar localmente antes de salir del servidor
NOM_ENC="ventaspos_${HOST}_${TS}.tar.gz.enc"
TAR_RAW="$TMP/payload.tar.gz"
ENC_TMP="$TMP/$NOM_ENC"
tar -czf "$TAR_RAW" -C "$TMP" datos config manifest.txt
openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt -in "$TAR_RAW" -out "$ENC_TMP" -pass file:"$PASS_FILE"
rm -f "$TAR_RAW"

mkdir -p "$DEST_LOCAL"
chmod 700 "$BACKUP_ROOT" "$DEST_LOCAL"
mv -f "$ENC_TMP" "$DEST_LOCAL/$NOM_ENC"
(cd "$DEST_LOCAL" && sha256sum "$NOM_ENC" > "$NOM_ENC.sha256")
chmod 600 "$DEST_LOCAL/$NOM_ENC" "$DEST_LOCAL/$NOM_ENC.sha256"

# Poda local
find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d -name '20*' -mtime +"$RETENCION_LOCAL_DIAS" -exec rm -rf {} +

SIZE_KB=$(( ($(stat -c%s "$DEST_LOCAL/$NOM_ENC") + 1023) / 1024 ))
log "Respaldo local cifrado OK: $DEST_LOCAL/$NOM_ENC (${SIZE_KB} KB, usuarios=$CANT_USUARIOS, tiendas=$CANT_TIENDAS)"

# 5. Subida cifrada a Google Drive (diarios y copia mensual los días 01)
if ! rclone copy "$DEST_LOCAL/$NOM_ENC" "${RCLONE_REMOTE}:diarios/$FECHA" --config "$RCLONE_CONFIG" \
   || ! rclone copy "$DEST_LOCAL/$NOM_ENC.sha256" "${RCLONE_REMOTE}:diarios/$FECHA" --config "$RCLONE_CONFIG"; then
  log "ERROR: falló la subida a Google Drive (copia local cifrada conservada en $DEST_LOCAL)"
  exit 1
fi

if [ "$DIA_MES" = "01" ]; then
  rclone copy "$DEST_LOCAL/$NOM_ENC" "${RCLONE_REMOTE}:mensuales/$MES" --config "$RCLONE_CONFIG"
  rclone copy "$DEST_LOCAL/$NOM_ENC.sha256" "${RCLONE_REMOTE}:mensuales/$MES" --config "$RCLONE_CONFIG"
  log "Copia mensual guardada en Drive: mensuales/$MES/$NOM_ENC"
fi

# Poda remota (respaldos > RETENCION_DRIVE_DIAS y mensuales > RETENCION_DRIVE_MESES meses)
rclone delete "${RCLONE_REMOTE}:diarios" --min-age "${RETENCION_DRIVE_DIAS}d" --config "$RCLONE_CONFIG" 2>/dev/null || true
rclone rmdirs "${RCLONE_REMOTE}:diarios" --leave-root --config "$RCLONE_CONFIG" 2>/dev/null || true
MESES_DIAS=$((RETENCION_DRIVE_MESES * 31))
rclone delete "${RCLONE_REMOTE}:mensuales" --min-age "${MESES_DIAS}d" --config "$RCLONE_CONFIG" 2>/dev/null || true
rclone rmdirs "${RCLONE_REMOTE}:mensuales" --leave-root --config "$RCLONE_CONFIG" 2>/dev/null || true

log "Subida a Google Drive OK -> diarios/$FECHA/$NOM_ENC"
EOS
chmod 750 /usr/local/bin/respaldar-ventasapi

# 7. Cron diario 02:30 VET
echo "30 2 * * * root /usr/local/bin/respaldar-ventasapi >> /var/log/ventasreportesapi-respaldo.log 2>&1" > /etc/cron.d/$APP
chmod 644 /etc/cron.d/$APP
touch /var/log/ventasreportesapi-respaldo.log
chmod 640 /var/log/ventasreportesapi-respaldo.log

echo
echo "Ejecutando respaldo de prueba inmediato hacia Google Drive..."
/usr/local/bin/respaldar-ventasapi
echo
verde "Archivos recientes en Google Drive (${REMOTE_NAME}:diarios):"
rclone lsl "${REMOTE_NAME}:diarios" --config "$RCLONE_CONFIG" | tail -n 6
echo
verde "Listo. Cron diario programado a las 02:30 (registro en /var/log/ventasreportesapi-respaldo.log)."
