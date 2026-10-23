#!/usr/bin/env bash
# Prueba automática: el Nginx del contenedor «web» y el sitio del Nginx del host registran SIN query string y con la
# IP reducida (RUNBOOK §3, docs/03 «Logs»). No toca nada de la instalación: contenedores efímeros sin red.
#   infra/tests/nginx-logs.sh [IMAGEN_WEB] [--from-checkout]
#     IMAGEN_WEB       por defecto alvarez-finanzas-web:local
#     --from-checkout  monta infra/nginx/web/*.conf del checkout sobre los de la imagen (probar sin reconstruir)
set -euo pipefail
cd "$(dirname "$0")/../.."
web_img="alvarez-finanzas-web:local"; from_checkout=0
for a in "$@"; do case "$a" in --from-checkout) from_checkout=1 ;; *) web_img="$a" ;; esac; done
edge_img="${EDGE_IMAGE:-nginx:1.30.6-alpine3.24}"
secret="busqueda-secreta-$RANDOM$RANDOM"
name="alvarez-logtest-$$"
tmp="$(mktemp -d)"
cleanup() { docker rm -f "$name-web" "$name-edge" >/dev/null 2>&1 || true; rm -rf "$tmp"; }
trap cleanup EXIT
fail() { echo "FALLA: $*" >&2; exit 1; }
ok() { echo "OK    $*"; }

wait_http() { # contenedor url: espera cualquier respuesta HTTP
  local code
  for _ in $(seq 1 30); do
    code="$(docker exec "$1" curl -s -o /dev/null -w '%{http_code}' "$2" 2>/dev/null || true)"
    [[ -n "$code" && "$code" != 000 ]] && return 0
    sleep 0.5
  done
  docker logs "$1" >&2; fail "$1 no respondió en $2"
}

check_log() { # descripción log ip_completa ip_reducida patrón_línea
  local what="$1" log="$2" full="$3" reduced="$4" line="$5"
  if grep -q "$secret" <<<"$log"; then fail "$what: el log contiene la query string"; fi
  if grep -qF "$full " <<<"$log"; then fail "$what: el log contiene la IP completa $full"; fi
  if grep -qiE 'curl/|Mozilla' <<<"$log"; then fail "$what: el log contiene el User-Agent"; fi
  if grep -qF 198.51.100.77 <<<"$log"; then fail "$what: el log contiene un X-Forwarded-For no confiable"; fi
  grep -qE "^$reduced \[[^]]+\] $line" <<<"$log" || { echo "$log" >&2; fail "$what: falta la línea esperada «$reduced … $line»"; }
  ok "$what: sin query string, sin User-Agent, IP $full registrada como ${reduced//\\/}"
}

# 1. Contenedor web. Sin red: «api» apunta a una dirección inalcanzable (502 inmediato, sin bucles).
mounts=()
if (( from_checkout )); then
  mounts=(-v "$PWD/infra/nginx/web/default.conf:/etc/nginx/conf.d/default.conf:ro"
          -v "$PWD/infra/nginx/web/security-headers.conf:/etc/nginx/security-headers.conf:ro")
fi
docker run -d --name "$name-web" --network none --add-host api:192.0.2.1 --read-only --tmpfs /tmp \
  --cap-drop ALL --security-opt no-new-privileges:true "${mounts[@]}" "$web_img" >/dev/null
wait_http "$name-web" http://127.0.0.1:8080/healthz
docker exec "$name-web" curl -s -o /dev/null -H "X-Forwarded-For: 198.51.100.77" "http://127.0.0.1:8080/movimientos?q=$secret&desde=2026-01-01"
docker exec "$name-web" curl -s -o /dev/null "http://127.0.0.1:8080/api/v1/entries?q=$secret"
sleep 1
log="$(docker logs "$name-web" 2>/dev/null)"
check_log "web (SPA)" "$log" 127.0.0.1 '127\.0\.0\.0' '"GET /movimientos" 200 '
check_log "web (/api)" "$log" 127.0.0.1 '127\.0\.0\.0' '"GET /api/v1/entries" 502 '
grep -q '/healthz' <<<"$log" && fail "web: /healthz no debería registrarse"

# 2. Sitio del Nginx del host (misma plantilla que instala install-nginx-site.sh), con TLS autofirmado.
mkdir -p "$tmp/certs"
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost" \
  -keyout "$tmp/certs/privkey.pem" -out "$tmp/certs/fullchain.pem" 2>/dev/null
chmod 644 "$tmp/certs/privkey.pem"
sed -e 's#CERT-DIR#/certs#g' -e 's/finanzas\.TU-DOMINIO/localhost/g' -e 's/127\.0\.0\.1:8088/192.0.2.1:8088/g' \
  -e '/listen \[::\]/d' infra/nginx/finanzas.conf > "$tmp/site.conf"
docker run -d --name "$name-edge" --network none \
  -v "$tmp/site.conf:/etc/nginx/conf.d/default.conf:ro" -v "$PWD/infra/nginx/alvarez-proxy.conf:/etc/nginx/snippets/alvarez-proxy.conf:ro" \
  -v "$tmp/certs:/certs:ro" "$edge_img" >/dev/null
wait_http "$name-edge" http://localhost/.well-known/acme-challenge/
docker exec "$name-edge" curl -s -o /dev/null --cacert /certs/fullchain.pem -H "X-Forwarded-For: 198.51.100.77" "https://localhost/movimientos?q=$secret"
docker exec "$name-edge" curl -s -o /dev/null "http://localhost/movimientos?q=$secret"
sleep 1
log="$(docker exec "$name-edge" cat /var/log/nginx/alvarez-finanzas.access.log)"
check_log "host (443)" "$log" 127.0.0.1 '127\.0\.0\.0' '"GET /movimientos" 502 '
check_log "host (80)" "$log" 127.0.0.1 '127\.0\.0\.0' '"GET /movimientos" 301 '
default_log="$(docker logs "$name-edge" 2>/dev/null)"
if grep -q "$secret" <<<"$default_log"; then fail "host: el log por defecto de Nginx recibió la petición con query string"; fi
ok "host: nada de la app en el log por defecto de Nginx"
echo "Logs de Nginx OK"
