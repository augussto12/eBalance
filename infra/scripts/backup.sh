#!/usr/bin/env bash
# Backup consistente (pg_dump) + key ring, cifrado por restic antes de salir del contenedor. Retención 10/7d/4s/3m.
# Además: configuración no secreta (etiqueta «config») y copia al repositorio externo si OFFSITE_RESTIC_REPOSITORY
# está en .env (credenciales en SECRETS_DIR/restic-offsite.env). Programado a diario por alvarez-backup.timer.
#   BACKUP_NO_PRUNE=1    no borra instantáneas locales (backup de seguridad antes de restaurar)
#   BACKUP_LOCAL_ONLY=1  no copia al repositorio externo (lo usa restore.sh: una caída del destino no frena la restauración)
#   backup.sh --verify-offsite   solo verifica que el último dump del repositorio externo se lea y sea igual al local
# Correr como root (sudo): ajusta los permisos que necesita el contenedor de backup, que corre sin root.
source "$(dirname "$0")/lib.sh"
load_env
umask 077
prepare_backup_perms
if [[ "${1:-}" == --verify-offsite ]]; then
  offsite_configured || die "No hay repositorio externo configurado (OFFSITE_RESTIC_REPOSITORY en .env)."
  backup_ops offsite-verify
  exit 0
fi
dc --profile ops run --rm -T backup init </dev/null
dc --profile ops run --rm -T -e BACKUP_NO_PRUNE="${BACKUP_NO_PRUNE:-0}" backup backup </dev/null

# Configuración NO secreta para reconstruir el servidor: .env, sitio de Nginx instalado, unidades systemd y versión.
# Los secretos (SECRETS_DIR) no van acá: se guardan fuera del servidor (RUNBOOK §4).
stage=""
trap 'if [[ -n "$stage" ]]; then rm -rf "$stage"; fi' EXIT
backup_config() {
  local site
  if [[ $EUID -ne 0 ]]; then warn "Sin root no se respalda la configuración del servidor (etiqueta config)."; return 0; fi
  stage="$(mktemp -d)"
  mkdir -p "$stage/nginx" "$stage/systemd"
  cp -p .env "$stage/env"
  [[ -f .release-history ]] && cp -p .release-history "$stage/release-history"
  git -C "$ROOT" rev-parse HEAD > "$stage/commit" 2>/dev/null || true
  for site in /etc/nginx/sites-available/zz-alvarez-finanzas.conf /etc/nginx/conf.d/zz-alvarez-finanzas.conf /etc/nginx/snippets/alvarez-proxy.conf; do
    [[ -f "$site" ]] && cp -p "$site" "$stage/nginx/"
  done
  find /etc/systemd/system -maxdepth 1 -name 'alvarez-*' -type f -exec cp -p {} "$stage/systemd/" \; 2>/dev/null || true
  chown -R "0:$BACKUP_UID" "$stage"
  chmod -R u=rwX,g=rX,o= "$stage"
  backup_ops -v "$stage:/config:ro" -- backup-config || die "Falló el backup de la configuración"
  rm -rf "$stage"; stage=""
}
backup_config

offsite=no-configurada
if [[ "${BACKUP_LOCAL_ONLY:-0}" == 1 ]]; then
  offsite=omitida
elif offsite_configured; then
  info "Copiando instantáneas al repositorio externo"
  if backup_ops offsite-copy; then offsite=OK; else offsite=FALLA; fi
else
  warn "Sin copia fuera de la VPS (OFFSITE_RESTIC_REPOSITORY vacío): el único backup está en este servidor. Ver RUNBOOK §4."
fi
echo "$(date -u +%FT%TZ) backup OK offsite=$offsite" >> "$BACKUP_DIR/backup.log"
[[ "$offsite" != FALLA ]] || die "Falló la copia al repositorio externo (el backup local quedó bien). Ver journalctl -u alvarez-backup."
