#!/bin/sh
# Operaciones de restic que corren DENTRO del contenedor de backup (usuario 1654, sin capacidades).
# No se copia a la imagen: los scripts del host (lib.sh, backup_ops) lo montan de solo lectura desde el checkout, así
# funciona igual con la imagen de backup de un release anterior (rollback). Uso:
#   backup-ops.sh offsite-copy                         copia las instantáneas al repositorio externo (lo crea si no existe)
#   backup-ops.sh offsite-verify                       el último dump externo se lee completo, es válido y es igual al local
#   backup-ops.sh backup-config                        respalda /config (configuración no secreta) con la etiqueta «config»
#   backup-ops.sh restore-config SNAP DESTINO          extrae una instantánea «config» en DESTINO
#   backup-ops.sh snapshots local|offsite
#   backup-ops.sh key-check local|offsite ARCHIVO      ¿la contraseña de ARCHIVO abre el repositorio?
#   backup-ops.sh key-add local|offsite ARCHIVO        agrega ARCHIVO como clave nueva (autentica con la vigente)
#   backup-ops.sh key-remove-for local|offsite ARCHIVO quita la clave que abre ARCHIVO (autentica con la vigente)
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

# ID de la clave que abre el repositorio con la contraseña del archivo $1 (vacío si no lo abre).
key_id_for() {
  RESTIC_PASSWORD_FILE="$1" restic key list --json 2>/dev/null | tr '}' '\n' | grep '"current":true' \
    | sed -n 's/.*"id":"\([^"]*\)".*/\1/p' || true
}

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
    local_sum="$(RESTIC_REPOSITORY="$LOCAL_REPO" restic dump --host "$HOST_TAG" --tag db latest alvarez.dump | sha256sum | cut -d' ' -f1)"
    use_repo offsite
    open_offsite
    restic snapshots --host "$HOST_TAG" --latest 1
    # Se lee el dump completo desde el repositorio externo: pg_restore valida el formato sin escribir nada.
    restic dump --host "$HOST_TAG" --tag db latest alvarez.dump | pg_restore --list >/dev/null
    off_sum="$(restic dump --host "$HOST_TAG" --tag db latest alvarez.dump | sha256sum | cut -d' ' -f1)"
    [ "$local_sum" = "$off_sum" ] || die "El último dump externo no coincide con el local (¿falta copiar el último backup?)."
    echo "Copia externa verificada: el último dump se lee y coincide con el local."
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
    RESTIC_PASSWORD_FILE="$pw" restic cat config >/dev/null || die "La contraseña de $pw no abre el repositorio ${2}."
    ;;
  key-add)
    use_repo "${2:-}"
    new="${3:?Falta el archivo con la contraseña nueva}"
    restic key add --new-password-file "$new" --host "$HOST_TAG" --user alvarez-finanzas
    [ -n "$(key_id_for "$new")" ] || die "La clave nueva no abre el repositorio ${2}."
    ;;
  key-remove-for)
    use_repo "${2:-}"
    old="${3:?Falta el archivo con la contraseña a retirar}"
    cur_id="$(key_id_for "$RESTIC_PASSWORD_FILE")"
    [ -n "$cur_id" ] || die "La contraseña vigente no abre el repositorio ${2}: no se quita nada."
    old_id="$(key_id_for "$old")"
    if [ -z "$old_id" ]; then echo "La contraseña anterior ya no abre el repositorio ${2}: nada que quitar."; exit 0; fi
    [ "$old_id" != "$cur_id" ] || die "La contraseña anterior y la vigente son la misma clave: no se quita."
    restic key remove "$old_id"
    echo "Clave anterior quitada del repositorio ${2}."
    ;;
  *)
    echo "Comando desconocido: ${1:-} (ver el encabezado de backup-ops.sh)" >&2; exit 2 ;;
esac
