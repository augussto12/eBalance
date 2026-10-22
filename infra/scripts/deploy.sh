#!/usr/bin/env bash
# Despliegue: build con tag inmutable (commit) → preflight → backup → migración (rol migrador) → arranque → smoke tests.
#   ./infra/scripts/deploy.sh                 despliega el commit actual
#   ./infra/scripts/deploy.sh --first         primer despliegue (sin backup previo; TLS aún no verificado)
#   ./infra/scripts/deploy.sh --rollback TAG  vuelve a un tag anterior SIN migrar (ver RUNBOOK: compatibilidad de esquema)
source "$(dirname "$0")/lib.sh"
load_env
mode="${1:-deploy}"

set_tag() {
  if grep -q '^IMAGE_TAG=' .env; then sed -i "s/^IMAGE_TAG=.*/IMAGE_TAG=$1/" .env; else echo "IMAGE_TAG=$1" >> .env; fi
  export IMAGE_TAG="$1"
}

wait_healthy() {
  local svc="$1" id status
  for _ in $(seq 1 45); do
    id="$(dc ps -q "$svc")"
    status="$(docker inspect -f '{{.State.Health.Status}}' "$id" 2>/dev/null || echo starting)"
    [[ "$status" == healthy ]] && { info "$svc healthy"; return 0; }
    sleep 2
  done
  dc logs --tail 50 "$svc" >&2
  die "$svc no quedó healthy"
}

smoke() {
  local base="http://${WEB_BIND:-127.0.0.1:8088}"
  curl -fsS "$base/healthz" >/dev/null || die "web no responde"
  [[ "$(curl -fsS "$base/api/health")" == *ok* ]] || die "API no responde"
  curl -fsSI "$base/" | grep -qi "content-security-policy: default-src 'none'" || die "falta CSP en /"
  code=$(curl -s -o /dev/null -w '%{http_code}' "$base/api/v1/entries"); [[ "$code" == 401 ]] || die "/api/v1/entries sin sesión devolvió $code (esperado 401)"
  code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$base/api/v1/dev/demo"); [[ "$code" != 200 ]] || die "endpoint de datos ficticios expuesto"
  if [[ "${SKIP_TLS_CHECK:-0}" != 1 ]]; then
    curl -fsS "$PUBLIC_ORIGIN/api/health" >/dev/null || die "$PUBLIC_ORIGIN no responde por HTTPS"
  fi
  info "Smoke tests OK"
}

case "$mode" in
  --rollback)
    tag="${2:?Indicá el tag}"
    docker image inspect "alvarez-finanzas-api:$tag" >/dev/null || die "No existe la imagen alvarez-finanzas-api:$tag"
    warn "El rollback NO revierte migraciones. Si el esquema cambió de forma incompatible, restaurá desde backup (RUNBOOK)."
    confirm_word ROLLBACK "Volver a $tag."
    set_tag "$tag"
    dc up -d api web
    wait_healthy api; wait_healthy web; smoke
    exit 0;;
  deploy|--first) ;;
  *) die "Uso: deploy.sh [--first | --rollback TAG]";;
esac

git diff --quiet && git diff --cached --quiet || die "Hay cambios sin commitear: el tag de imagen no sería reproducible."
tag="$(git rev-parse --short=12 HEAD)"
previous="${IMAGE_TAG:-}"

[[ "$mode" == --first ]] && export SKIP_TLS_CHECK=1
"$ROOT/infra/scripts/preflight.sh"

info "Construyendo imágenes $tag"
docker build -f infra/docker/api.Dockerfile -t "alvarez-finanzas-api:$tag" .
docker build -f infra/docker/web.Dockerfile -t "alvarez-finanzas-web:$tag" .
docker build -f infra/docker/backup.Dockerfile -t "alvarez-finanzas-backup:$tag" .

if [[ "$mode" != --first ]]; then
  info "Backup previo a la migración"
  "$ROOT/infra/scripts/backup.sh"
fi

set_tag "$tag"
dc up -d db
wait_healthy db
info "Migraciones (rol migrador)"
dc --profile ops run --rm migrate
dc up -d api web
wait_healthy api
wait_healthy web
smoke
echo "$(date -u +%FT%TZ) $tag (anterior: ${previous:-ninguno})" >> .release-history
# Imágenes viejas de ESTA app: se conservan la actual y la anterior (rollback). No se toca nada de otros proyectos.
for repo in alvarez-finanzas-api alvarez-finanzas-web alvarez-finanzas-backup; do
  for old in $(docker image ls "$repo" --format '{{.Tag}}' | grep -vxF -e "$tag" -e "${previous:-$tag}" || true); do
    docker image rm "$repo:$old" >/dev/null 2>&1 || true
  done
done
info "Desplegado $tag. Rollback: ./infra/scripts/deploy.sh --rollback ${previous:-<tag>}"
