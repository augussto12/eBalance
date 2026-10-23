#!/bin/sh
# Uso (dentro del contenedor de backup): alvarez-backup backup | snapshots | restore-dump <snapshot> <archivo> | restore-keys <snapshot> <dir> | check
# «backup» sale con 3 si respaldó la base pero no el key ring (/keys sin claves); con otro código distinto de 0, falló.
set -eu
: "${RESTIC_REPOSITORY:?Falta RESTIC_REPOSITORY}"
export RESTIC_PASSWORD_FILE=/run/secrets/restic_password
export PGPASSWORD_FILE=/run/secrets/db_migrator_password
HOST_TAG="${BACKUP_HOST_TAG:-alvarez-finanzas}"

# ¿/keys tiene al menos una clave de Data Protection (key-<guid>.xml)?
has_keys() {
  for f in /keys/key-*.xml; do
    [ -f "$f" ] && return 0
  done
  return 1
}

case "${1:-backup}" in
  init)
    restic cat config >/dev/null 2>&1 && { echo "Repositorio ya inicializado."; exit 0; }
    restic init
    ;;
  backup)
    started=$(date +%s)
    # --stdin-from-command: si pg_dump falla, restic NO guarda una instantánea parcial.
    PGPASSWORD="$(cat "$PGPASSWORD_FILE")" restic backup --host "$HOST_TAG" --tag db --stdin-filename alvarez.dump \
      --stdin-from-command -- pg_dump -h db -U alvarez_migrator -d alvarez --format=custom --no-owner --no-privileges
    # Key ring de Data Protection (sus claves están cifradas con el certificado, que NO se respalda acá). Un /keys sin
    # key-*.xml (volumen perdido o recreado vacío: la API falla cerrado y no escribe claves) NO se respalda: pasaría a ser
    # la instantánea «keyring» más reciente y, con la retención, terminaría desplazando a las buenas. La base sí se
    # respalda; al final sale con 3 para que backup.sh siga (configuración y copia externa) y termine con error (alerta).
    # BACKUP_SKIP_KEYRING=1 (backup de seguridad de restore.sh): no se respalda el key ring.
    keyring=OK
    if [ "${BACKUP_SKIP_KEYRING:-0}" = 1 ]; then
      keyring=OMITIDO
      echo "Key ring no respaldado (BACKUP_SKIP_KEYRING=1)."
    elif has_keys; then
      restic backup --host "$HOST_TAG" --tag keyring /keys
    else
      keyring=VACIO
      echo "ERROR: /keys no tiene claves (key-*.xml) o no se puede leer: el key ring NO se respalda. ¿Se perdió el volumen «keys»? Ver RUNBOOK §5 (restore.sh --keyring-only)." >&2
    fi
    # --keep-last 10: varias instantáneas del mismo día (p. ej. backup previo a migrar o restaurar) no se pisan.
    # BACKUP_NO_PRUNE=1 (backup de seguridad antes de restaurar): no se borra nada.
    if [ "${BACKUP_NO_PRUNE:-0}" != 1 ]; then
      restic forget --host "$HOST_TAG" --group-by host,tags --keep-last 10 --keep-daily 7 --keep-weekly 4 --keep-monthly 3 --prune
    fi
    restic check --read-data-subset=5%
    if [ "$keyring" = VACIO ]; then
      echo "Backup de la base OK en $(( $(date +%s) - started )) s; key ring SIN respaldar (/keys vacío)." >&2
      exit 3
    fi
    echo "Backup OK en $(( $(date +%s) - started )) s"
    ;;
  snapshots)
    restic snapshots --host "$HOST_TAG"
    ;;
  resolve)
    # resolve <db|keyring> <snapshot|latest> → ID corto fijo (para no confundir «latest» con un backup posterior).
    tag="${2:?db|keyring}"; snap="${3:-latest}"
    if [ "$snap" = latest ]; then
      restic snapshots --host "$HOST_TAG" --tag "$tag" --latest 1 --json | grep -o '"short_id":"[^"]*"' | tail -1 | cut -d'"' -f4
    else
      echo "$snap"
    fi
    ;;
  restore-dump)
    snap="${2:-latest}"; out="${3:?Falta archivo destino}"
    restic dump --host "$HOST_TAG" --tag db "$snap" alvarez.dump > "$out"
    ;;
  restore-keys)
    snap="${2:-latest}"; dest="${3:?Falta directorio destino}"
    restic restore --host "$HOST_TAG" --tag keyring "$snap" --target "$dest"
    ;;
  check)
    restic check
    ;;
  *)
    echo "Comando desconocido: $1" >&2; exit 2;;
esac
