#!/usr/bin/env bash
# Prepara el entorno E2E local: renderiza finanzas.conf para https://localhost:8443 → web:8080 y un certificado autofirmado.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p rendered/certs
sed -e 's/finanzas\.TU-DOMINIO/localhost/g' -e 's/127\.0\.0\.1:8088/web:8080/g' -e '/listen \[::\]/d' ../nginx/finanzas.conf > rendered/finanzas.conf
if [[ ! -s rendered/certs/fullchain.pem ]]; then
  openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 30 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost" \
    -keyout rendered/certs/privkey.pem -out rendered/certs/fullchain.pem 2>/dev/null
fi
echo "E2E listo: rendered/finanzas.conf y certificado autofirmado."
