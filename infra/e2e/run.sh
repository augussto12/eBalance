#!/usr/bin/env bash
# Corre el E2E completo desde cero sobre el stack real: borra los volúmenes del proyecto LOCAL (nunca usar en la VPS),
# migra, crea un propietario ficticio y ejecuta Playwright. Requiere .env con COMPOSE_FILE que incluya compose.e2e.yaml.
set -euo pipefail
source "$(dirname "$0")/../scripts/lib.sh"
load_env
[[ "${COMPOSE_FILE:-}" == *compose.e2e.yaml* ]] || die "Este script es solo para el entorno E2E local (COMPOSE_FILE con compose.e2e.yaml)."
[[ "$PUBLIC_ORIGIN" == https://localhost:* ]] || die "PUBLIC_ORIGIN debe ser https://localhost:PUERTO en E2E."
"$ROOT/infra/e2e/prepare.sh"
dc down -v --remove-orphans
"$ROOT/infra/scripts/migrate.sh"
dc up -d
for _ in $(seq 1 60); do [[ "$(docker inspect -f '{{.State.Health.Status}}' "$(dc ps -q web)" 2>/dev/null)" == healthy ]] && break; sleep 2; done
E2E_PASSWORD="${E2E_PASSWORD:-contraseña de prueba larga para e2e 2026}"
printf '%s\n' "$E2E_PASSWORD" | "$ROOT/infra/scripts/bootstrap-owner.sh" --username "${E2E_USER:-augusto}" --display-name "Augusto Álvarez"
rm -f "$ROOT/tests/e2e/.e2e-state.json"
cd "$ROOT/tests/e2e"
[[ -d node_modules ]] || npm ci --no-audit --no-fund
E2E_PASSWORD="$E2E_PASSWORD" npx playwright test "$@"
