#!/usr/bin/env bash
# Chequeo liviano cada hora (alvarez-healthcheck.timer): db/api/web healthy, la web y la API responden por loopback y
# hay espacio en disco (repo, datos de Docker, BACKUP_DIR). No abre la base ni corre contenedores nuevos.
# Sale con error → la unidad falla → alvarez-alert@ registra y, si está configurado, avisa. Sin contenido financiero.
source "$(dirname "$0")/lib.sh"
load_env
status=0
fail() { warn "$*"; status=1; }
# shellcheck disable=SC2329  # la llama check_disks
note() { echo "$*"; }
for svc in db api web; do
  state="$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q "$svc" 2>/dev/null)" 2>/dev/null || true)"
  if [[ "$state" == healthy ]]; then echo "$svc healthy"; else fail "Servicio $svc: ${state:-no está corriendo}"; fi
done
base="http://${WEB_BIND:-127.0.0.1:8088}"
if curl -fsS --max-time 10 -o /dev/null "$base/healthz"; then echo "web responde"; else fail "La web no responde en $base"; fi
if [[ "$(curl -fsS --max-time 10 "$base/api/health" 2>/dev/null || true)" == *ok* ]]; then echo "API responde"; else fail "La API no responde en $base/api/health"; fi
check_disks note fail || true
exit $status
