#!/usr/bin/env bash
# Certificados TLS con certbot oficial en contenedor (versión fijada por digest), con configuración AISLADA en
# /etc/alvarez-finanzas/letsencrypt: no toca ni depende del certbot que el servidor tenga para otros sitios.
#   certbot.sh issue ip 31.97.241.220      certificado para la IP (perfil shortlived, ~6 días; LE GA desde 2026-01-15)
#   certbot.sh issue host srv123.hstgr.cloud   certificado normal (90 días) para un nombre
#   certbot.sh renew                        renueva si corresponde (lo llama el timer alvarez-certbot) y recarga Nginx
# Desafío http-01 por webroot (/var/www/certbot), servido por el bloque :80 del sitio de Nginx.
# Solo para pruebas locales contra Pebble (ACME de prueba de Let's Encrypt): ACME_SERVER, ACME_CA_BUNDLE, CERTBOT_NETWORK.
set -euo pipefail
CERTBOT_IMAGE="${CERTBOT_IMAGE:-certbot/certbot:v5.8.0@sha256:f70ad0adbb7e117f0fe42a63c553f28ea451edabc0148757b6efcd9735acaa20}"
LE_DIR=/etc/alvarez-finanzas/letsencrypt
CERT_NAME=alvarez-finanzas
WEBROOT=/var/www/certbot

run_certbot() {
  mkdir -p "$LE_DIR" /var/lib/alvarez-finanzas/letsencrypt /var/log/alvarez-finanzas/letsencrypt "$WEBROOT"
  chmod 700 "$LE_DIR"
  local extra=() acme=()
  [[ -n "${CERTBOT_NETWORK:-}" ]] && extra+=(--network "$CERTBOT_NETWORK")
  [[ -n "${ACME_CA_BUNDLE:-}" ]] && extra+=(-v "$ACME_CA_BUNDLE:/acme-ca.pem:ro" -e REQUESTS_CA_BUNDLE=/acme-ca.pem)
  [[ -n "${ACME_SERVER:-}" && "$1" != renew ]] && acme+=(--server "$ACME_SERVER")
  docker run --rm --name "alvarez-certbot-$$" "${extra[@]}" \
    -v "$LE_DIR:/etc/letsencrypt" \
    -v /var/lib/alvarez-finanzas/letsencrypt:/var/lib/letsencrypt \
    -v /var/log/alvarez-finanzas/letsencrypt:/var/log/letsencrypt \
    -v "$WEBROOT:$WEBROOT" \
    "$CERTBOT_IMAGE" "$@" "${acme[@]}"
}

case "${1:-}" in
  issue)
    kind="${2:?ip|host}"; name="${3:?IP o nombre}"
    email_args=(--register-unsafely-without-email)
    [[ -n "${ACME_EMAIL:-}" ]] && email_args=(--email "$ACME_EMAIL")
    if [[ "$kind" == ip ]]; then
      # «required»: si el perfil no existiera, falla con un error claro (los certificados de IP exigen shortlived).
      id_args=(--ip-address "$name" --required-profile shortlived)
    else
      id_args=(-d "$name")
    fi
    common=(certonly --non-interactive --agree-tos "${email_args[@]}" --webroot -w "$WEBROOT" --cert-name "$CERT_NAME" "${id_args[@]}")
    # Ensayo previo contra el entorno de pruebas de Let's Encrypt: si el puerto 80 no llega (firewall del panel,
    # NAT), falla acá sin consumir el límite de validaciones fallidas de producción.
    if [[ -z "${ACME_SERVER:-}" && "${SKIP_DRY_RUN:-0}" != 1 ]]; then
      echo "Ensayo (staging) de la validación http-01 para $name…"
      run_certbot "${common[@]}" --dry-run
    fi
    run_certbot "${common[@]}" --keep-until-expiring
    ;;
  renew)
    # El deploy-hook (dentro del contenedor) marca que hubo renovación; solo entonces se recarga Nginx.
    flag="$LE_DIR/.renewed"
    rm -f "$flag"
    run_certbot renew --non-interactive --no-random-sleep-on-renew --deploy-hook "touch /etc/letsencrypt/.renewed"
    if [[ -e "$flag" ]]; then
      rm -f "$flag"
      # Recarga graciosa solo si la configuración es válida (no corta conexiones de otros sitios).
      if nginx -t >/dev/null 2>&1; then systemctl reload nginx; echo "Certificado renovado; Nginx recargado."
      else echo "nginx -t falló: no se recargó" >&2; exit 1; fi
    fi
    ;;
  *)
    echo "Uso: certbot.sh issue ip|host NOMBRE | renew" >&2; exit 2 ;;
esac
