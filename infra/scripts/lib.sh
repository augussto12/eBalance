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
