#!/usr/bin/env bash
# Prueba automática de install-nginx-site.sh --render (no toca /etc/nginx ni necesita root ni .env): reinstalar el sitio
# conserva las decisiones del sitio instalado (listen [::] y default_server del 443), que el instalador tomó según lo que
# ya escuchaba el Nginx del host; las variables explícitas siguen mandando.
#   infra/tests/nginx-site.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
fail() { echo "FALLA: $*" >&2; exit 1; }
ok() { echo "OK    $*"; }
render() { # [VAR=valor…] -- ACTUAL: sitio que se instalaría, con un entorno limpio de NO_IPV6/NO_IPV6_443/DEFAULT_443
  local vars=()
  while [[ "$1" != -- ]]; do vars+=("$1"); shift; done
  shift
  env -u NO_IPV6 -u NO_IPV6_443 -u DEFAULT_443 "${vars[@]}" CERT_DIR=/etc/alvarez-finanzas/letsencrypt/live/alvarez-finanzas \
    infra/scripts/install-nginx-site.sh --render finanzas.example.test "$@"
}
has() { grep -qE "$2" <<<"$1"; }
v6_80='^ *listen \[::\]:80;'
v6_443='^ *listen \[::\]:443 ssl'
default443='^ *listen 443 ssl.*default_server'

# 1. Sin sitio instalado: la plantilla completa (con IPv6 y sin default_server).
out="$(render -- "$tmp/no-existe.conf")"
has "$out" "$v6_80" && has "$out" "$v6_443" && ! has "$out" "$default443" || fail "sin sitio instalado: $out"
ok "sin sitio instalado: plantilla con listen [::] y sin default_server"

# 2. Sitio de dominio instalado por vps-install.sh en un host cuyo Nginx no escucha en IPv6 (NO_IPV6=1).
render NO_IPV6=1 NO_IPV6_443=1 -- "$tmp/no-existe.conf" > "$tmp/v4.conf"
has "$(cat "$tmp/v4.conf")" 'listen \[::\]' && fail "NO_IPV6=1 dejó un listen [::]"
out="$(render -- "$tmp/v4.conf")"
has "$out" 'listen \[::\]' && fail "reinstalar un sitio solo IPv4 agregó listen [::] (pasaría a ser el default de IPv6 de otros sitios)"
has "$out" 'listen 80;' && has "$out" 'listen 443 ssl' || fail "faltan los listen IPv4: $out"
ok "reinstalar un sitio solo IPv4 no agrega listen [::]"

# 3. Host que escucha [::]:80 pero no [::]:443 (NO_IPV6_443=1).
render NO_IPV6= NO_IPV6_443=1 -- "$tmp/no-existe.conf" > "$tmp/v6-80.conf"
out="$(render -- "$tmp/v6-80.conf")"
has "$out" "$v6_80" && ! has "$out" "$v6_443" || fail "reinstalar con solo [::]:80: $out"
ok "reinstalar conserva [::]:80 sin agregar [::]:443"

# 4. Sitio por IP (default del 443, sin IPv6).
render DEFAULT_443=1 NO_IPV6=1 -- "$tmp/no-existe.conf" > "$tmp/ip.conf"
out="$(render -- "$tmp/ip.conf")"
has "$out" "$default443" && ! has "$out" 'listen \[::\]' || fail "reinstalar el sitio por IP: $out"
ok "reinstalar el sitio por IP conserva default_server y no agrega IPv6"

# 5. Las variables explícitas mandan sobre el sitio instalado (vacías o 0: con IPv6 / sin default_server).
out="$(render NO_IPV6= NO_IPV6_443=0 DEFAULT_443= -- "$tmp/ip.conf")"
has "$out" "$v6_80" && has "$out" "$v6_443" && ! has "$out" "$default443" || fail "las variables explícitas no mandaron: $out"
ok "las variables explícitas mandan sobre el sitio instalado"

# 6. El sitio con IPv6 completo se reinstala igual.
render -- "$tmp/no-existe.conf" > "$tmp/v6.conf"
cmp -s "$tmp/v6.conf" <(render -- "$tmp/v6.conf") || fail "reinstalar el sitio con IPv6 cambió el contenido"
ok "reinstalar un sitio con IPv6 no lo cambia"
echo "Sitio de Nginx OK"
