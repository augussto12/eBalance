#!/usr/bin/env bash
# Instala el sitio en el Nginx EXISTENTE del host sin tocar nginx.conf ni otros sitios. Valida con nginx -t antes de recargar.
#   sudo ./infra/scripts/install-nginx-site.sh finanzas.midominio.com [--force]
source "$(dirname "$0")/lib.sh"
load_env
domain="${1:?Indicá el dominio}"
[[ "$domain" =~ ^[a-z0-9.-]+$ ]] || die "Dominio no válido"
bind="${WEB_BIND:-127.0.0.1:8088}"
if [[ -d /etc/nginx/sites-available ]]; then target=/etc/nginx/sites-available/alvarez-finanzas.conf; link=/etc/nginx/sites-enabled/alvarez-finanzas.conf
else target=/etc/nginx/conf.d/alvarez-finanzas.conf; link=""; fi
rendered="$(mktemp)"; trap 'rm -f "$rendered"' EXIT
sed -e "s/finanzas\.TU-DOMINIO/$domain/g" -e "s/127\.0\.0\.1:8088/$bind/g" infra/nginx/finanzas.conf > "$rendered"
if [[ -f "$target" ]] && ! cmp -s "$rendered" "$target" && [[ "${2:-}" != --force ]]; then
  diff -u "$target" "$rendered" || true
  die "$target ya existe con otro contenido. Revisá el diff y usá --force para reemplazarlo."
fi
grep -rqs "zone=alvarez_auth" /etc/nginx --exclude="$(basename "$target")" && die "Otra configuración ya define la zona alvarez_auth."
[[ -f /etc/letsencrypt/live/$domain/fullchain.pem ]] || warn "No hay certificado para $domain. Obtenelo primero (ver RUNBOOK: certbot certonly --webroot)."
install -D -m 644 infra/nginx/alvarez-proxy.conf /etc/nginx/snippets/alvarez-proxy.conf
install -m 644 "$rendered" "$target"
[[ -n "$link" ]] && ln -sf "$target" "$link"
nginx -t || { rm -f "$link"; die "nginx -t falló: no se recargó Nginx."; }
systemctl reload nginx || nginx -s reload
info "Sitio instalado en $target"
