#!/bin/sh
# Operaciones de restic que corren DENTRO del contenedor de backup (usuario 1654, sin capacidades).
# No se copia a la imagen: los scripts del host (lib.sh, backup_ops) lo montan de solo lectura desde el checkout, así
# funciona igual con la imagen de backup de un release anterior (rollback). Uso:
#   backup-ops.sh offsite-copy                         copia las instantáneas al repositorio externo (lo crea si no existe)
#   backup-ops.sh offsite-verify                       el último dump externo se lee completo, es válido y es igual al local
#   backup-ops.sh resolve-keyring [ID|latest]          ID corto de la instantánea «keyring» más reciente (o la indicada)
#                                                      que TIENE claves; las de un /keys vacío se saltean
#   backup-ops.sh backup-config                        respalda /config (configuración no secreta) con la etiqueta «config»
#   backup-ops.sh restore-config SNAP DESTINO          extrae una instantánea «config» en DESTINO
#   backup-ops.sh snapshots local|offsite
#   backup-ops.sh key-check local|offsite ARCHIVO      ¿la contraseña de ARCHIVO abre el repositorio?
#   backup-ops.sh key-add local|offsite ARCHIVO        agrega ARCHIVO como clave nueva (autentica con la vigente)
#   backup-ops.sh key-remove-for local|offsite ARCHIVO quita la clave que abre ARCHIVO (autentica con la vigente).
#                                                      Sale con 3 sin borrar nada si el externo es de solo agregar (OFFSITE_PRUNE=0)
#   backup-ops.sh key-id local|offsite ARCHIVO         ID de la clave que abre ARCHIVO (vacío si no lo abre)
#   backup-ops.sh key-ids local|offsite                IDs de todas las claves del repositorio (autentica con la vigente)
# Credenciales del repositorio externo: /run/alvarez/offsite.env (archivo montado de solo lectura), líneas NOMBRE=valor.
# Se leen SIN ejecutarlas (no es «source») y solo se aceptan variables de los backends de restic y de proxy.
set -eu
# El sh de la imagen (BusyBox ash, Alpine) admite pipefail.
# shellcheck disable=SC3040
set -o pipefail
export RESTIC_PASSWORD_FILE="${RESTIC_PASSWORD_FILE:-/run/secrets/restic_password}"
HOST_TAG="${BACKUP_HOST_TAG:-alvarez-finanzas}"
LOCAL_REPO="${RESTIC_REPOSITORY:?Falta RESTIC_REPOSITORY}"
OFFSITE_ENV=/run/alvarez/offsite.env
CR="$(printf '\r')"

die() { echo "ERROR: $*" >&2; exit 1; }

load_offsite() {
  [ -n "${OFFSITE_RESTIC_REPOSITORY:-}" ] || die "OFFSITE_RESTIC_REPOSITORY no está definido en .env"
  if [ -f "$OFFSITE_ENV" ]; then
    n=0
    while IFS= read -r line || [ -n "$line" ]; do
      n=$((n + 1))
      line="${line%"$CR"}"
      case "$line" in ''|'#'*) continue ;; esac
      name="${line%%=*}"
      [ "$name" != "$line" ] || die "restic-offsite.env, línea $n: se espera NOMBRE=valor"
      case "$name" in
        AWS_*|B2_*|AZURE_*|OS_*|ST_*|RESTIC_REST_USERNAME|RESTIC_REST_PASSWORD|RESTIC_COMPRESSION|RESTIC_PACK_SIZE) ;;
        HTTPS_PROXY|https_proxy|HTTP_PROXY|http_proxy|NO_PROXY|no_proxy) ;;
        *) die "restic-offsite.env, línea $n: variable no admitida (solo credenciales de backends de restic; el repositorio va en OFFSITE_RESTIC_REPOSITORY y la contraseña es la de restic)" ;;
      esac
      case "$name" in *[!A-Za-z0-9_]*) die "restic-offsite.env, línea $n: nombre de variable no válido" ;; esac
      value="${line#*=}"
      case "$value" in
        \"*\") value="${value#\"}"; value="${value%\"}" ;;
        \'*\') value="${value#\'}"; value="${value%\'}" ;;
      esac
      export "$name=$value"
    done < "$OFFSITE_ENV"
  fi
  export RESTIC_REPOSITORY="$OFFSITE_RESTIC_REPOSITORY"
}

use_repo() {
  case "${1:-}" in
    local) export RESTIC_REPOSITORY="$LOCAL_REPO" ;;
    offsite) load_offsite ;;
    *) die "Repositorio desconocido: «${1:-}» (local|offsite)" ;;
  esac
}

# Misma retención que el repositorio local (backup-entrypoint.sh).
keep_policy() {
  restic forget --host "$HOST_TAG" --group-by host,tags --keep-last 10 --keep-daily 7 --keep-weekly 4 --keep-monthly 3 --prune
}

# Bloqueos: el backup diario, la copia externa y una poda remota toman un bloqueo exclusivo un rato; se espera.
RETRY_LOCK="--retry-lock=${RESTIC_RETRY_LOCK:-2m}"

# ID de la clave que abre el repositorio con la contraseña del archivo $1. Vacío SOLO si restic responde que esa contraseña
# no lo abre (código 12). Cualquier otro error (repositorio bloqueado, destino caído) termina con error: una falla pasajera
# no puede pasar por «la clave anterior ya no está». Llamarla como «x="$(key_id_for …)" || exit 1».
key_id_for() {
  kl_rc=0
  kl_out="$(RESTIC_PASSWORD_FILE="$1" restic "$RETRY_LOCK" key list --json 2>/tmp/keylist.err)" || kl_rc=$?
  case "$kl_rc" in
    0) printf '%s\n' "$kl_out" | tr '}' '\n' | grep '"current":true' | sed -n 's/.*"id":"\([0-9a-f]*\)".*/\1/p' || true ;;
    12) ;;
    *) sed 's/^/  /' /tmp/keylist.err >&2
       die "«restic key list» falló en el repositorio $RESTIC_REPOSITORY (código $kl_rc): no se sabe qué clave abre $1." ;;
  esac
}

# OFFSITE_PRUNE=0: el destino externo es de solo agregar (rest-server --append-only, bloqueo de objetos). Desde la VPS
# no se puede (ni se intenta) borrar nada ahí, tampoco una clave.
offsite_append_only() { [ "$1" = offsite ] && [ "${OFFSITE_PRUNE:-1}" != 1 ]; }

# Códigos de salida de restic: 10 = el repositorio no existe; 12 = la contraseña no lo abre.
open_offsite() {
  rc=0
  restic cat config >/dev/null 2>/tmp/offsite.err || rc=$?
  case "$rc" in
    0) ;;
    10)
      [ "${1:-}" = create ] || die "El repositorio externo no existe todavía (lo crea el primer backup.sh)."
      echo "Creando el repositorio externo (mismos parámetros de fragmentación que el local)"
      restic init --from-repo "$LOCAL_REPO" --from-password-file "$RESTIC_PASSWORD_FILE" --copy-chunker-params ;;
    12) die "La contraseña de restic no abre el repositorio externo. ¿Hay una rotación a medias? Ver RUNBOOK §7." ;;
    *) sed 's/^/  /' /tmp/offsite.err >&2; die "No se pudo acceder al repositorio externo (restic salió con $rc)." ;;
  esac
}

case "${1:-}" in
  offsite-copy)
    use_repo offsite
    open_offsite create
    restic copy --host "$HOST_TAG" --from-repo "$LOCAL_REPO" --from-password-file "$RESTIC_PASSWORD_FILE"
    # OFFSITE_PRUNE=0: destino de solo agregar (rest-server --append-only, bucket con bloqueo de objetos). Así un
    # atacante con root en la VPS no puede borrar las copias externas; la poda se hace desde otra máquina.
    if [ "${OFFSITE_PRUNE:-1}" = 1 ]; then keep_policy; else echo "Sin poda en el repositorio externo (OFFSITE_PRUNE=0)."; fi
    restic check
    echo "Copia externa OK"
    ;;
  offsite-verify)
    local_sum="$(RESTIC_REPOSITORY="$LOCAL_REPO" restic dump --host "$HOST_TAG" --tag db latest alvarez.dump | sha256sum | cut -d' ' -f1)" \
      || die "No se pudo leer el último dump del repositorio local."
    use_repo offsite
    open_offsite
    restic snapshots --host "$HOST_TAG" --latest 1
    # El último dump externo se descarga UNA vez y se lee completo: tee lo pasa a sha256sum (por un FIFO) y a
    # «pg_restore --list», que valida el encabezado y el índice del formato custom sin escribir nada. pg_restore deja de
    # leer al terminar el índice: el resto se vacía con cat para que restic y tee no reciban SIGPIPE (con pipefail, un
    # dump de más de ~64 KiB hacía fallar la verificación con el código 141 y sin mensaje).
    fifo=/tmp/offsite-dump.fifo; sumf=/tmp/offsite-dump.sha256
    rm -f "$fifo" "$sumf"
    mkfifo -m 600 "$fifo"
    sha256sum < "$fifo" > "$sumf" &
    sum_pid=$!
    rc=0
    restic dump --host "$HOST_TAG" --tag db latest alvarez.dump | tee "$fifo" \
      | { prc=0; pg_restore --list >/dev/null || prc=$?; cat >/dev/null; exit "$prc"; } || rc=$?
    wait "$sum_pid" || { [ "$rc" != 0 ] || rc=1; }
    rm -f "$fifo"
    [ "$rc" = 0 ] || die "El último dump externo no se pudo leer completo o no es un dump válido de PostgreSQL (código $rc)."
    off_sum="$(cut -d' ' -f1 "$sumf")"
    [ "$local_sum" = "$off_sum" ] || die "El último dump externo no coincide con el local (¿falta copiar el último backup?)."
    echo "Copia externa verificada: el último dump se lee completo, es un dump válido y coincide con el local."
    ;;
  resolve-keyring)
    # El key ring más reciente contiene también las claves viejas (no se borran), así que sirve para cualquier dump
    # anterior. Se saltean las instantáneas sin key-*.xml: un /keys vacío (volumen perdido) respaldado por una versión
    # anterior, que no sirve para restaurar. Con un ID, solo esa (tiene que ser del key ring y tener claves).
    use_repo local
    want="${2:-latest}"
    case "$want" in
      latest) want="" ;;
      *[!0-9a-f]*|?|??|???) die "ID de instantánea no válido: «$want» (hexadecimal, al menos 4 caracteres; ver «restic snapshots»)." ;;
    esac
    # restic lista de la más vieja a la más nueva; con un ID explícito ignora --tag, por eso se filtra acá.
    json="$(restic "$RETRY_LOCK" snapshots --host "$HOST_TAG" --tag keyring --json)" \
      || die "No se pudieron listar las instantáneas del key ring."
    ids="$(printf '%s\n' "$json" | grep -o '"id":"[0-9a-f]*"' | cut -d'"' -f4 | tac || true)"
    seen=0
    for id in $ids; do
      case "$id" in "$want"*) ;; *) continue ;; esac
      seen=1
      listing="$(restic "$RETRY_LOCK" ls "$id")" || die "No se pudo leer la instantánea $(echo "$id" | cut -c1-8)."
      if printf '%s\n' "$listing" | grep -c '^/keys/key-[^/]*\.xml$' >/dev/null; then
        echo "$id" | cut -c1-8
        exit 0
      fi
      echo "AVISO: la instantánea $(echo "$id" | cut -c1-8) del key ring no tiene claves (/keys vacío): se saltea." >&2
    done
    if [ -n "$want" ] && [ "$seen" = 0 ]; then die "$want no es una instantánea del key ring (etiqueta «keyring») de este servidor."; fi
    die "No hay ninguna instantánea del key ring con claves${want:+ con el ID $want}."
    ;;
  backup-config)
    use_repo local
    [ -d /config ] || die "Falta /config"
    restic backup --host "$HOST_TAG" --tag config /config
    ;;
  restore-config)
    use_repo local
    snap="${2:-latest}"; dest="${3:?Falta el directorio destino}"
    restic restore --host "$HOST_TAG" --tag config "$snap" --target "$dest"
    ;;
  snapshots)
    use_repo "${2:-local}"
    restic snapshots --host "$HOST_TAG"
    ;;
  key-check)
    use_repo "${2:-}"
    pw="${3:?Falta el archivo de contraseña}"
    # restic reintenta hasta ~15 min si el destino no responde; para esta comprobación previa alcanza con menos.
    RESTIC_PASSWORD_FILE="$pw" timeout "${KEY_CHECK_TIMEOUT:-90}" restic cat config >/dev/null \
      || die "El repositorio ${2} no responde o la contraseña de $pw no lo abre."
    ;;
  key-add)
    use_repo "${2:-}"
    new="${3:?Falta el archivo con la contraseña nueva}"
    restic "$RETRY_LOCK" key add --new-password-file "$new" --host "$HOST_TAG" --user alvarez-finanzas
    new_id="$(key_id_for "$new")" || exit 1
    [ -n "$new_id" ] || die "La clave nueva no abre el repositorio ${2}."
    ;;
  key-id)
    use_repo "${2:-}"
    pw="${3:?Falta el archivo de contraseña}"
    id="$(key_id_for "$pw")" || exit 1
    [ -z "$id" ] || echo "$id"
    ;;
  key-ids)
    use_repo "${2:-}"
    out="$(restic "$RETRY_LOCK" key list --json)" || die "«restic key list» falló en el repositorio ${2}."
    printf '%s\n' "$out" | grep -o '"id":"[0-9a-f]*"' | cut -d'"' -f4 || true
    ;;
  key-remove-for)
    use_repo "${2:-}"
    old="${3:?Falta el archivo con la contraseña a retirar}"
    cur_id="$(key_id_for "$RESTIC_PASSWORD_FILE")" || exit 1
    [ -n "$cur_id" ] || die "La contraseña vigente no abre el repositorio ${2}: no se quita nada."
    old_id="$(key_id_for "$old")" || exit 1
    if [ -z "$old_id" ]; then echo "La contraseña anterior ya no abre el repositorio ${2}: nada que quitar."; exit 0; fi
    [ "$old_id" != "$cur_id" ] || die "La contraseña anterior y la vigente son la misma clave: no se quita."
    if offsite_append_only "$2"; then
      echo "Destino externo de solo agregar (OFFSITE_PRUNE=0): no se borra la clave $old_id desde acá." >&2
      exit 3
    fi
    restic "$RETRY_LOCK" key remove "$old_id"
    echo "Clave anterior quitada del repositorio ${2}."
    ;;
  *)
    echo "Comando desconocido: ${1:-} (ver el encabezado de backup-ops.sh)" >&2; exit 2 ;;
esac
