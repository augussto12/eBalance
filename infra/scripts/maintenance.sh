#!/usr/bin/env bash
# Mantenimiento diario (alvarez-maintenance.timer):
#  1. Purga auditoría vieja (App:AuditRetentionDays, 90 por defecto) con el rol migrador: el rol runtime no puede borrarla.
#  2. Verifica TLS, espacio en disco (repo, datos de Docker y BACKUP_DIR), reloj (NTP), salud de los servicios, backup
#     local y copia externa recientes. Sale con error (→ unidad fallida → alvarez-alert@) si algo anda mal.
#  Las alertas NUNCA incluyen contenido financiero.
source "$(dirname "$0")/lib.sh"
load_env
status=0
fail() { warn "$*"; status=1; }
# shellcheck disable=SC2329  # la llama check_disks
note() { echo "$*"; }
dc --profile ops run --rm -T migrate purge-audit </dev/null || status=1
check_disks note fail || true
ntp_rc=0; msg="$(check_ntp)" || ntp_rc=$?
case "$ntp_rc" in 0) echo "$msg" ;; 1) fail "$msg" ;; *) warn "$msg" ;; esac
for svc in db api web; do
  [[ "$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q "$svc")" 2>/dev/null)" == healthy ]] || fail "Servicio $svc no está healthy"
done
if msg="$(check_tls)"; then echo "$msg"; else fail "$msg"; fi

# Antigüedad en segundos del último registro de backup.log que cumple el patrón (vacío si no hay ninguno).
age_of_last() {
  local ts
  ts="$(grep -E -- "$1" "$BACKUP_DIR/backup.log" 2>/dev/null | tail -1 | cut -d' ' -f1 || true)"
  if [[ -n "$ts" ]]; then echo $(( $(date +%s) - $(date -d "$ts" +%s) )); fi
}
age="$(age_of_last ' backup OK')"
if [[ -z "$age" ]] || (( age > 2*86400 )); then fail "No hay backup exitoso en las últimas 48 h"; fi
if offsite_configured; then
  age="$(age_of_last ' offsite=OK')"
  if [[ -z "$age" ]] || (( age > 2*86400 )); then fail "No hay copia exitosa al repositorio externo en las últimas 48 h"; fi
else
  warn "Sin copia de backups fuera de la VPS (OFFSITE_RESTIC_REPOSITORY): el único backup está en este servidor (RUNBOOK §4)."
fi
prev="$SECRETS_DIR/restic_password.previous"
if [[ -e "$prev" ]] && (( $(date +%s) - $(stat -c %Y "$prev") > 7*86400 )); then
  warn "Rotación de restic sin confirmar hace más de 7 días: guardá la contraseña nueva y corré rotate-secrets.sh restic-confirm."
fi
exit $status
