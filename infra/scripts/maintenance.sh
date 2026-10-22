#!/usr/bin/env bash
# Mantenimiento semanal:
#  1. Purga auditoría vieja (App:AuditRetentionDays, 90 por defecto) con el rol migrador: el rol runtime no puede borrarla.
#  2. Verifica vencimiento de TLS, espacio en disco y salud de los servicios. Sale con error (→ unidad systemd fallida) si algo anda mal.
#  Las alertas NUNCA incluyen contenido financiero.
source "$(dirname "$0")/lib.sh"
load_env
status=0
dc --profile ops run --rm -T migrate purge-audit || status=1
avail_kb=$(df -Pk "$ROOT" | awk 'NR==2{print $4}')
(( avail_kb > 2*1024*1024 )) || { warn "Menos de 2 GiB libres"; status=1; }
for svc in db api web; do
  [[ "$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q "$svc")" 2>/dev/null)" == healthy ]] || { warn "Servicio $svc no está healthy"; status=1; }
done
hostport="${PUBLIC_ORIGIN#https://}"; host="${hostport%%:*}"; [[ "$hostport" == *:* ]] || hostport="$hostport:443"
if end=$(echo | timeout 10 openssl s_client -servername "$host" -connect "$hostport" 2>/dev/null | openssl x509 -noout -enddate 2>/dev/null); then
  days=$(( ( $(date -d "${end#notAfter=}" +%s) - $(date +%s) ) / 86400 ))
  (( days > 14 )) || { warn "El certificado TLS vence en $days días"; status=1; }
else
  warn "No se pudo leer el certificado TLS de $host"; status=1
fi
last_backup=$(tail -1 "$BACKUP_DIR/backup.log" 2>/dev/null | cut -d' ' -f1)
if [[ -z "$last_backup" ]] || (( $(date +%s) - $(date -d "$last_backup" +%s) > 2*86400 )); then warn "No hay backup exitoso en las últimas 48 h"; status=1; fi
exit $status
