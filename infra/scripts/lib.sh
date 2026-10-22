#!/usr/bin/env bash
# Funciones comunes. Los scripts se ejecutan desde cualquier lugar; ROOT es la raíz del repo.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

die() { echo "ERROR: $*" >&2; exit 1; }
info() { echo "==> $*"; }
warn() { echo "AVISO: $*" >&2; }

load_env() {
  [[ -f .env ]] || die "Falta .env (copiá .env.example y completalo)."
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
  SECRETS_DIR="${SECRETS_DIR:-./secrets}"
  BACKUP_DIR="${BACKUP_DIR:-./backups}"
}

# COMPOSE_FILE (en .env) permite agregar overlays, p. ej. el entorno E2E local.
dc() {
  if [[ -n "${COMPOSE_FILE:-}" ]]; then docker compose --env-file .env "$@"
  else docker compose --env-file .env -f compose.yaml "$@"; fi
}

# Confirmación explícita escribiendo una palabra (no acepta «y» por defecto).
confirm_word() {
  local word="$1" prompt="$2" answer
  # Desde la terminal si existe (los comandos de Docker pueden consumir stdin).
  if [[ -r /dev/tty ]] && { : < /dev/tty; } 2>/dev/null; then
    read -r -p "$prompt Escribí «$word» para continuar: " answer < /dev/tty
  else
    read -r -p "$prompt Escribí «$word» para continuar: " answer
  fi
  [[ "$answer" == "$word" ]] || die "Cancelado."
}

# UIDs de las imágenes: postgres (alpine) = 70; .NET aspnet «app» = 1654.
PG_UID=70
APP_UID=1654

# Verifica el certificado TLS público: cadena de confianza, que cubra el nombre/IP de PUBLIC_ORIGIN y vencimiento.
# Umbral proporcional a su vida útil: los de IP duran ~6 días y certbot los renueva a mitad de vida (alerta con
# < 2 días restantes); los normales, 90 (alerta con < 14). TLS_CA_FILE: CA propia (solo pruebas). Devuelve 0/1.
check_tls() {
  local hostport="${PUBLIC_ORIGIN#https://}" host out cert code start end now left life min
  host="${hostport%%:*}"; [[ "$hostport" == *:* ]] || hostport="$hostport:443"
  local sni=(-servername "$host") verify=(-verify_hostname "$host") ca=()
  if [[ "$host" =~ ^[0-9.]+$ ]]; then sni=(); verify=(-verify_ip "$host"); fi
  [[ -n "${TLS_CA_FILE:-}" ]] && ca=(-CAfile "$TLS_CA_FILE")
  out="$(echo | timeout 10 openssl s_client "${sni[@]}" "${verify[@]}" "${ca[@]}" -connect "$hostport" 2>&1 || true)"
  cert="$(openssl x509 -noout -startdate -enddate 2>/dev/null <<<"$out")" \
    || { echo "No se pudo leer el certificado TLS de $hostport"; return 1; }
  code="$(sed -n 's/^ *Verify return code: \([0-9]*\).*/\1/p' <<<"$out" | tail -1)"
  if [[ "$code" != 0 ]]; then
    echo "El certificado TLS de $hostport no es válido para $host ($(grep -m1 -o 'Verify return code: .*' <<<"$out" || echo 'sin verificación'))"
    return 1
  fi
  start=$(date -d "$(sed -n 's/^notBefore=//p' <<<"$cert")" +%s); end=$(date -d "$(sed -n 's/^notAfter=//p' <<<"$cert")" +%s); now=$(date +%s)
  left=$(( (end - now) / 3600 )); life=$(( (end - start) / 3600 ))
  min=$(( life <= 240 ? 48 : 14 * 24 ))
  if (( left < min )); then echo "El certificado TLS vence en ${left} h (vida ${life} h): revisá la renovación (certbot renew)"; return 1; fi
  echo "TLS válido: ${left} h restantes de ${life} h"
}
