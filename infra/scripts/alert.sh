#!/usr/bin/env bash
# Aviso de falla de una unidad systemd (OnFailure=alvarez-alert@%n.service en cada servicio alvarez-*).
# SIEMPRE lo registra en el journal y en /var/log/alvarez-finanzas/alerts.log. Además notifica SOLO si el propietario
# configuró un destino en .env (no hay ninguno por defecto):
#   ALERT_WEBHOOK_URL       https://… que recibe un POST de texto plano (p. ej. un tema de ntfy propio o un webhook).
#                           Cabeceras opcionales (token) en SECRETS_DIR/alert-webhook.headers, una por línea, modo 400.
#   ALERT_WEBHOOK_CA_FILE   CA propia del destino (opcional).
#   ALERT_EMAIL             dirección para el sendmail local (solo si hay uno instalado y configurado).
#   ALERT_REPEAT_HOURS      no repite el aviso de la misma unidad antes de N horas (6 por defecto).
# El mensaje es fijo: unidad, servidor y hora. NUNCA lleva contenido financiero ni extractos de logs.
#   ./infra/scripts/alert.sh alvarez-backup.service     (lo llama systemd)
#   sudo ./infra/scripts/alert.sh --test                envía un aviso de prueba al destino configurado
source "$(dirname "$0")/lib.sh"
if [[ -f .env ]]; then load_env; else warn "Falta .env: solo se registra el aviso."; SECRETS_DIR="${SECRETS_DIR:-./secrets}"; fi
unit="${1:?Uso: alert.sh UNIDAD | --test}"
log_dir="${ALERT_LOG_DIR:-/var/log/alvarez-finanzas}"
state_dir="${ALERT_STATE_DIR:-/var/lib/alvarez-finanzas/alerts}"
now="$(date -u +%FT%TZ)"
host="$(hostname -s 2>/dev/null || hostname)"
kind=FALLA
if [[ "$unit" == --test ]]; then
  kind=PRUEBA
  msg="Álvarez Finanzas: aviso de PRUEBA desde $host ($now). Si te llegó, las alertas funcionan."
else
  [[ "$unit" =~ ^[A-Za-z0-9@._-]+$ ]] || die "Nombre de unidad no válido"
  msg="Álvarez Finanzas: falló $unit en $host ($now). Revisá en el servidor: journalctl -u $unit"
fi

mkdir -p "$log_dir" "$state_dir"; chmod 750 "$log_dir"; chmod 700 "$state_dir"
echo "$now ${unit} $kind" >> "$log_dir/alerts.log"
echo "$msg"
if command -v logger >/dev/null; then logger -t alvarez-alert -p user.err -- "$msg" || true; fi

if [[ -z "${ALERT_WEBHOOK_URL:-}" && -z "${ALERT_EMAIL:-}" ]]; then
  echo "Sin destino de avisos configurado (ALERT_WEBHOOK_URL / ALERT_EMAIL en .env): queda solo en el registro."
  exit 0
fi
stamp="$state_dir/${unit//[^A-Za-z0-9@._-]/_}.last"
repeat_s=$(( ${ALERT_REPEAT_HOURS:-6} * 3600 ))
if [[ "$unit" != --test && -e "$stamp" ]] && (( $(date +%s) - $(stat -c %Y "$stamp") < repeat_s )); then
  echo "Ya se avisó de $unit hace menos de ${ALERT_REPEAT_HOURS:-6} h: no se repite."
  exit 0
fi

sent=0 failed=0
if [[ -n "${ALERT_WEBHOOK_URL:-}" ]]; then
  if [[ "$ALERT_WEBHOOK_URL" != https://* ]]; then
    warn "ALERT_WEBHOOK_URL debe ser https://: no se envió."; failed=1
  else
    args=(-fsS --max-time 20 --proto "=https" --proto-redir "=https" -o /dev/null -H "Content-Type: text/plain; charset=utf-8")
    headers="$SECRETS_DIR/alert-webhook.headers"
    [[ -f "$headers" ]] && args+=(-H "@$headers")
    [[ -n "${ALERT_WEBHOOK_CA_FILE:-}" ]] && args+=(--cacert "$ALERT_WEBHOOK_CA_FILE")
    if curl "${args[@]}" --data-binary "$msg" "$ALERT_WEBHOOK_URL"; then sent=1; else warn "No se pudo enviar el aviso al webhook."; failed=1; fi
  fi
fi
if [[ -n "${ALERT_EMAIL:-}" ]]; then
  if command -v sendmail >/dev/null; then
    if printf 'To: %s\nSubject: %s\nContent-Type: text/plain; charset=utf-8\n\n%s\n' "$ALERT_EMAIL" "Álvarez Finanzas: aviso de $host" "$msg" | sendmail -t -oi; then
      sent=1
    else warn "sendmail no pudo enviar el aviso."; failed=1; fi
  else
    warn "ALERT_EMAIL está definido pero no hay sendmail instalado."; failed=1
  fi
fi
if (( sent )); then touch "$stamp"; echo "$now ${unit} aviso enviado" >> "$log_dir/alerts.log"; fi
exit $failed
