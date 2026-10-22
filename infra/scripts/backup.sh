#!/usr/bin/env bash
# Backup consistente (pg_dump) + key ring, cifrado por restic antes de salir del contenedor. Retención 7d/4s/3m.
# Programar diario con cron/systemd (ver RUNBOOK). Para copia fuera de la VPS: OFFSITE_RESTIC_REPOSITORY en .env.
source "$(dirname "$0")/lib.sh"
load_env
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
dc --profile ops run --rm -T backup init
dc --profile ops run --rm -T -e BACKUP_NO_PRUNE="${BACKUP_NO_PRUNE:-0}" backup backup
if [[ -n "${OFFSITE_RESTIC_REPOSITORY:-}" ]]; then
  info "Copiando instantáneas al repositorio externo"
  env_args=()
  [[ -f "$SECRETS_DIR/restic-offsite.env" ]] && env_args=(--env-file "$SECRETS_DIR/restic-offsite.env")
  dc --profile ops run --rm -T "${env_args[@]}" -e RESTIC_REPOSITORY="$OFFSITE_RESTIC_REPOSITORY" --entrypoint sh backup -c \
    'export RESTIC_PASSWORD_FILE=/run/secrets/restic_password; restic cat config >/dev/null 2>&1 || restic init --from-repo /backups/restic --from-password-file /run/secrets/restic_password --copy-chunker-params; restic copy --from-repo /backups/restic --from-password-file /run/secrets/restic_password && restic forget --keep-last 10 --keep-daily 7 --keep-weekly 4 --keep-monthly 3 --prune'
fi
echo "$(date -u +%FT%TZ) backup OK" >> "$BACKUP_DIR/backup.log"
