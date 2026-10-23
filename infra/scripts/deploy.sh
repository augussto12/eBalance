#!/usr/bin/env bash
# Despliegue: build con tag inmutable (commit) → preflight → backup → migración (rol migrador) → arranque → smoke tests.
#   ./infra/scripts/deploy.sh                 despliega el commit actual
#   ./infra/scripts/deploy.sh --first         primer despliegue (sin backup previo; TLS aún no verificado)
#   ./infra/scripts/deploy.sh --rollback TAG  vuelve a un tag anterior SIN migrar (ver RUNBOOK: compatibilidad de esquema)
#   ./infra/scripts/deploy.sh --smoke         solo los smoke tests contra lo que está corriendo
source "$(dirname "$0")/lib.sh"
load_env
mode="${1:-deploy}"

set_tag() {
  if grep -q '^IMAGE_TAG=' .env; then sed -i "s/^IMAGE_TAG=.*/IMAGE_TAG=$1/" .env; else echo "IMAGE_TAG=$1" >> .env; fi
  export IMAGE_TAG="$1"
}

# Cabeceras de seguridad que deben estar en el index, los assets y la API (infra/nginx/web/security-headers.conf).
SEC_HEADERS=("content-security-policy: default-src 'none'" "x-content-type-options: nosniff" "referrer-policy: no-referrer"
  "x-frame-options: deny" "permissions-policy: camera=()" "cross-origin-opener-policy: same-origin" "cross-origin-resource-policy: same-origin")
check_headers() { # dónde cabeceras
  local h
  for h in "${SEC_HEADERS[@]}"; do grep -qiF -- "$h" <<<"$2" || die "falta la cabecera «$h» en $1"; done
}

# Smoke tests sin sesión (no hay credenciales en el servidor): salud, cabeceras, un asset real del build, la API privada
# cerrada y sin caché, errores de la API como JSON (no el SPA) y sin el endpoint de datos ficticios.
smoke() {
  local base="http://${WEB_BIND:-127.0.0.1:8088}" tmp headers asset code tls=()
  tmp="$(mktemp -d)"
  curl -fsS "$base/healthz" >/dev/null || die "web no responde"
  [[ "$(curl -fsS "$base/api/health")" == *ok* ]] || die "API no responde"
  headers="$(curl -fsS -D - -o "$tmp/index.html" "$base/")" || die "/ no responde"
  check_headers / "$headers"
  grep -qi '^cache-control: no-cache' <<<"$headers" || die "el index no tiene Cache-Control: no-cache"
  asset="$(grep -oE '/assets/[A-Za-z0-9._-]+\.js' "$tmp/index.html" | head -1 || true)"
  [[ -n "$asset" ]] || die "index.html no referencia ningún /assets/*.js"
  headers="$(curl -fsS -D - -o "$tmp/asset" "$base$asset")" || die "no se pudo descargar $asset"
  [[ -s "$tmp/asset" ]] || die "$asset está vacío"
  grep -qiE '^content-type: (application|text)/javascript' <<<"$headers" || die "$asset no se sirve como JavaScript"
  grep -qi '^cache-control: .*immutable' <<<"$headers" || die "$asset sin caché inmutable"
  check_headers "$asset" "$headers"
  headers="$(curl -sS -D - -o /dev/null "$base/api/v1/entries")"
  code="$(head -1 <<<"$headers" | awk '{print $2}')"
  [[ "$code" == 401 ]] || die "/api/v1/entries sin sesión devolvió $code (esperado 401)"
  grep -qi '^cache-control: no-store' <<<"$headers" || die "la API no responde con Cache-Control: no-store"
  check_headers /api/v1/entries "$headers"
  headers="$(curl -sS -D - -o /dev/null "$base/api/v1/no-existe")"
  grep -qi '^content-type: application/problem+json' <<<"$headers" || die "un error de la API no se devolvió como JSON (¿cayó en el SPA?)"
  code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$base/api/v1/dev/demo"); [[ "$code" != 200 ]] || die "endpoint de datos ficticios expuesto"
  if [[ "${SKIP_TLS_CHECK:-0}" != 1 ]]; then
    [[ -z "${TLS_CA_FILE:-}" ]] || tls=(--cacert "$TLS_CA_FILE")
    curl -fsS "${tls[@]}" "$PUBLIC_ORIGIN/api/health" >/dev/null || die "$PUBLIC_ORIGIN no responde por HTTPS"
    headers="$(curl -fsS "${tls[@]}" -D - -o /dev/null "$PUBLIC_ORIGIN/")" || die "$PUBLIC_ORIGIN/ no responde por HTTPS"
    check_headers "$PUBLIC_ORIGIN/" "$headers"
  fi
  rm -rf "$tmp"
  info "Smoke tests OK (salud, cabeceras, asset $asset, API privada sin caché, sin datos ficticios)"
}

case "$mode" in
  --smoke) smoke; exit 0;;
  --rollback)
    tag="${2:?Indicá el tag}"
    # Las tres imágenes tienen que existir: compose no debe compilar nada con un tag viejo (--no-build).
    for img in api web backup; do
      docker image inspect "alvarez-finanzas-$img:$tag" >/dev/null 2>&1 || die "No existe la imagen alvarez-finanzas-$img:$tag"
    done
    previous="${IMAGE_TAG:-}"
    warn "El rollback NO revierte migraciones. Si el esquema cambió de forma incompatible, restaurá desde backup (RUNBOOK)."
    confirm_word ROLLBACK "Volver a $tag."
    set_tag "$tag"
    dc up -d --no-build api web
    wait_healthy api; wait_healthy web; smoke
    echo "$(date -u +%FT%TZ) $tag (rollback desde ${previous:-ninguno})" >> .release-history
    info "Rollback a $tag hecho. Para volver: ./infra/scripts/deploy.sh --rollback ${previous:-<tag>}"
    exit 0;;
  deploy|--first) ;;
  *) die "Uso: deploy.sh [--first | --rollback TAG | --smoke]";;
esac

git diff --quiet && git diff --cached --quiet || die "Hay cambios sin commitear: el tag de imagen no sería reproducible."
tag="$(git rev-parse --short=12 HEAD)"
previous="${IMAGE_TAG:-}"

[[ "$mode" == --first ]] && export SKIP_TLS_CHECK=1
"$ROOT/infra/scripts/preflight.sh"

info "Construyendo imágenes $tag"
# EXTRA_CA_FILE (opcional, igual que infra/e2e/build-images.sh): CA de un proxy TLS corporativo, solo en el build.
build_args=()
if [[ -n "${EXTRA_CA_FILE:-}" ]]; then
  build_args+=(--network host --secret "id=extra_ca,src=$EXTRA_CA_FILE")
  [[ -n "${HTTPS_PROXY:-}" ]] && build_args+=(--build-arg "HTTPS_PROXY=$HTTPS_PROXY" --build-arg "NO_PROXY=${NO_PROXY:-}")
fi
for img in api web backup; do
  DOCKER_BUILDKIT=1 docker build "${build_args[@]}" -f "infra/docker/$img.Dockerfile" -t "alvarez-finanzas-$img:$tag" .
done

if [[ "$mode" != --first ]]; then
  info "Backup previo a la migración"
  "$ROOT/infra/scripts/backup.sh"
fi

set_tag "$tag"
dc up -d db
wait_healthy db
info "Migraciones (rol migrador)"
dc --profile ops run --rm migrate
dc up -d --no-build api web
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
