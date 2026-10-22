#!/usr/bin/env bash
# Instala el sitio en el Nginx EXISTENTE del host sin tocar nginx.conf ni otros sitios. Valida con nginx -t antes y
# después; si algo falla, restaura el estado anterior (nunca deja una configuración rota activa).
#   sudo ./infra/scripts/install-nginx-site.sh NOMBRE [--force]        sitio completo (requiere certificado)
#   sudo ./infra/scripts/install-nginx-site.sh --acme-only NOMBRE      solo :80 para el desafío http-01 (temporal)
#   sudo ./infra/scripts/install-nginx-site.sh --remove-acme           quita el sitio temporal
# Variables: CERT_DIR (directorio con fullchain.pem/privkey.pem; por defecto /etc/letsencrypt/live/<nombre>),
#            NO_IPV6=1 (omite «listen [::]»), NO_IPV6_443=1 (solo omite [::]:443), DEFAULT_443=1 (certificado de IP: este sitio es el default del 443).
source "$(dirname "$0")/lib.sh"
load_env

if [[ -d /etc/nginx/sites-available ]]; then avail=/etc/nginx/sites-available; enabled=/etc/nginx/sites-enabled
else avail=/etc/nginx/conf.d; enabled=""; fi
site=zz-alvarez-finanzas.conf
acme_site=zz-alvarez-finanzas-acme.conf

reload_nginx() { systemctl reload nginx 2>/dev/null || nginx -s reload; }

# Instala un archivo de sitio de forma transaccional: respaldo → instalar → nginx -t → (si falla) restaurar.
install_site() {
  local name="$1" rendered="$2" target="$avail/$1" backup="" had_link=0
  nginx -t >/dev/null 2>&1 || die "nginx -t ya falla ANTES de instalar (otro sitio está roto). No toco nada: revisá «nginx -t»."
  if [[ -f "$target" ]]; then backup="$(mktemp)"; cp -p "$target" "$backup"; fi
  if [[ -n "$enabled" && -L "$enabled/$name" ]]; then had_link=1; fi
  install -m 644 "$rendered" "$target"
  if [[ -n "$enabled" ]]; then ln -sf "$target" "$enabled/$name"; fi
  if ! nginx -t; then
    if [[ -n "$backup" ]]; then cp -p "$backup" "$target"; else rm -f "$target"; fi
    if [[ -n "$enabled" && $had_link -eq 0 ]]; then rm -f "$enabled/$name"; fi
    rm -f "$backup"
    nginx -t >/dev/null 2>&1 || warn "nginx -t sigue fallando después de restaurar; revisá «nginx -t»."
    die "nginx -t falló con $name: se restauró el estado anterior y no se recargó Nginx."
  fi
  rm -f "$backup"
  reload_nginx
}

remove_acme_files() {
  rm -f "$avail/$acme_site"
  if [[ -n "$enabled" ]]; then rm -f "$enabled/$acme_site"; fi
}

case "${1:-}" in
  --remove-acme)
    remove_acme_files
    if nginx -t >/dev/null 2>&1; then reload_nginx; fi
    exit 0 ;;
  --acme-only)
    domain="${2:?Indicá el nombre o la IP}"
    [[ "$domain" =~ ^[a-z0-9.-]+$ ]] || die "Nombre no válido"
    mkdir -p /var/www/certbot
    rendered="$(mktemp)"; trap 'rm -f "$rendered"' EXIT
    {
      echo "# Temporal: solo el desafío http-01 de Let's Encrypt para $domain. Lo quita el instalador."
      echo "server {"
      echo "    listen 80;"
      if [[ -z "${NO_IPV6:-}" ]]; then echo "    listen [::]:80;"; fi
      echo "    server_name $domain;"
      echo "    location /.well-known/acme-challenge/ { root /var/www/certbot; default_type text/plain; }"
      echo "    location / { return 404; }"
      echo "}"
    } > "$rendered"
    install_site "$acme_site" "$rendered"
    # La recarga de Nginx es asíncrona: se espera a que el sitio responda antes de pedir el certificado.
    mkdir -p /var/www/certbot/.well-known/acme-challenge
    probe="alvarez-probe-$$"
    echo ok > "/var/www/certbot/.well-known/acme-challenge/$probe"
    ready=0
    for _ in $(seq 1 20); do
      if [[ "$(curl -s --max-time 2 -H "Host: $domain" "http://127.0.0.1/.well-known/acme-challenge/$probe" || true)" == ok ]]; then ready=1; break; fi
      sleep 0.5
    done
    rm -f "/var/www/certbot/.well-known/acme-challenge/$probe"
    [[ $ready -eq 1 ]] || warn "El sitio temporal no respondió en http://127.0.0.1 para $domain (¿Nginx escucha el 80 solo en otra dirección?)."
    info "Sitio temporal ACME para $domain"
    exit 0 ;;
esac

domain="${1:?Indicá el dominio o la IP}"
[[ "$domain" =~ ^[a-z0-9.-]+$ ]] || die "Dominio no válido"
cert_dir="${CERT_DIR:-/etc/letsencrypt/live/$domain}"
[[ "$cert_dir" =~ ^/[A-Za-z0-9._/-]+$ ]] || die "CERT_DIR no válido"
bind="${WEB_BIND:-127.0.0.1:8088}"
target="$avail/$site"
rendered="$(mktemp)"; trap 'rm -f "$rendered"' EXIT
extra=()
if [[ -n "${NO_IPV6:-}" ]]; then extra=(-e '/listen \[::\]/d')
elif [[ -n "${NO_IPV6_443:-}" ]]; then extra=(-e '/listen \[::\]:443/d'); fi
# «http2 on;» existe desde nginx 1.25.1; Ubuntu 24.04 trae 1.24 → se usa «listen 443 ssl http2».
ngx_version="$(nginx -v 2>&1 | grep -o '[0-9][0-9.]*' | head -1 || true)"
if [[ "$(printf '%s\n1.25.1\n' "$ngx_version" | sort -V | head -1)" != 1.25.1 ]]; then
  extra+=(-e '/^ *http2 on;/d' -e 's/listen 443 ssl;/listen 443 ssl http2;/' -e 's/listen \[::\]:443 ssl;/listen [::]:443 ssl http2;/')
fi
# Certificado de IP: los navegadores no envían SNI para IPs, así que este servidor debe ser el default del 443.
if [[ "${DEFAULT_443:-}" == 1 ]]; then
  extra+=(-e 's/listen 443 ssl/listen 443 ssl default_server/' -e 's/listen \[::\]:443 ssl/listen [::]:443 ssl default_server/')
fi
sed -e "s#CERT-DIR#$cert_dir#g" -e "s/finanzas\.TU-DOMINIO/$domain/g" -e "s/127\.0\.0\.1:8088/$bind/g" \
  "${extra[@]}" infra/nginx/finanzas.conf > "$rendered"
if [[ -f "$target" ]] && ! cmp -s "$rendered" "$target" && [[ "${2:-}" != --force ]]; then
  diff -u "$target" "$rendered" || true
  die "$target ya existe con otro contenido. Revisá el diff y usá --force para reemplazarlo."
fi
if grep -rls "zone=alvarez_auth" /etc/nginx 2>/dev/null | grep -v -e "/$site\$" -e "/$acme_site\$" | grep -q .; then
  die "Otra configuración ya define la zona alvarez_auth."
fi
[[ -f "$cert_dir/fullchain.pem" ]] || die "No hay certificado en $cert_dir. Obtenelo primero (infra/scripts/certbot.sh issue …)."
install -D -m 644 infra/nginx/alvarez-proxy.conf /etc/nginx/snippets/alvarez-proxy.conf
mkdir -p /var/www/certbot
# El sitio final ya sirve el desafío en :80: el temporal sobra (y su server_name duplicaría el del final).
remove_acme_files
install_site "$site" "$rendered"
info "Sitio instalado en $target"
