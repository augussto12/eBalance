#!/usr/bin/env bash
# Prueba de la copia externa CON archivo de credenciales, contra un repositorio externo SUSTITUTO: restic rest-server
# con usuario y contraseña, conectado a la red «egress» del proyecto. No cambia .env: usa variables de entorno y un
# archivo de credenciales temporal que se borra al terminar. Corre sobre una instalación existente (sudo).
#   sudo infra/tests/offsite-backup.sh                   copia, verificación y casos de error
#   sudo infra/tests/offsite-backup.sh --rotate-restic   además ROTA la contraseña de restic (las dos fases) y verifica
#                                                        que la vieja deja de abrir ambos repositorios. Solo en pruebas:
#                                                        la contraseña nueva queda en SECRETS_DIR/restic_password.
source "$(dirname "$0")/../scripts/lib.sh"
load_env
[[ $EUID -eq 0 ]] || die "Correr con sudo (backup.sh ajusta permisos)."
[[ -z "$(sed -n 's/^OFFSITE_RESTIC_REPOSITORY=//p' .env)" ]] || die "La instalación ya tiene OFFSITE_RESTIC_REPOSITORY en .env: esta prueba usa su propio destino."
rotate=0; [[ "${1:-}" == --rotate-restic ]] && rotate=1
REST_IMAGE="${REST_IMAGE:-restic/rest-server:0.14.0@sha256:d2aff06f47eb38637dff580c3e6bce4af98f386c396a25d32eb6727ec96214a5}"
name="alvarez-offsite-test-$$"
net="${COMPOSE_PROJECT_NAME:-alvarez-finanzas}_egress"
tmp="$(mktemp -d)"
cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  docker run --rm --user 0 -v "$tmp:/w" --entrypoint sh "$REST_IMAGE" -c 'rm -rf /w/*' >/dev/null 2>&1 || true
  rm -rf "$tmp"
}
trap cleanup EXIT
ok() { echo "OK    $*"; }
fail() { echo "FALLA $*" >&2; exit 1; }
last_log() { tail -1 "$BACKUP_DIR/backup.log"; }

info "Repositorio externo sustituto (rest-server con autenticación) en la red $net"
dc --profile ops run --rm -T backup init </dev/null >/dev/null   # crea las redes del proyecto si faltan
user=alvarez-offsite; pass="$(openssl rand -hex 24)"
mkdir -p "$tmp/data"
docker run --rm --entrypoint htpasswd "$REST_IMAGE" -nbB "$user" "$pass" > "$tmp/data/.htpasswd"
docker run -d --name "$name" --network "$net" --network-alias alvarez-offsite-test -v "$tmp/data:/data" "$REST_IMAGE" >/dev/null
for _ in $(seq 1 20); do docker logs "$name" 2>&1 | grep -qi 'starting server' && break; sleep 0.5; done
export OFFSITE_RESTIC_REPOSITORY="rest:http://alvarez-offsite-test:8000/alvarez"
export OFFSITE_RESTIC_ENV_FILE="$tmp/offsite.env"

# 1. Credenciales que no son de backends de restic: se rechazan sin ejecutarse (no es «source»).
printf 'RESTIC_PASSWORD=x\n' > "$OFFSITE_RESTIC_ENV_FILE"; chown "0:$BACKUP_UID" "$OFFSITE_RESTIC_ENV_FILE"; chmod 440 "$OFFSITE_RESTIC_ENV_FILE"
if backup_ops offsite-copy >"$tmp/out" 2>&1; then fail "aceptó RESTIC_PASSWORD en el archivo de credenciales"; fi
grep -q 'variable no admitida' "$tmp/out" || { cat "$tmp/out"; fail "error inesperado con una variable no admitida"; }
ok "el archivo de credenciales no puede redefinir la contraseña ni el repositorio"
# 2. Sin credenciales válidas, el destino rechaza la copia y backup.sh termina con error (alerta) sin perder el local.
printf '# comentario\nRESTIC_REST_USERNAME=%s\nRESTIC_REST_PASSWORD="incorrecta"\n' "$user" > "$OFFSITE_RESTIC_ENV_FILE"
if "$ROOT/infra/scripts/backup.sh" </dev/null >"$tmp/out" 2>&1; then cat "$tmp/out"; fail "backup.sh terminó bien con credenciales incorrectas"; fi
[[ "$(last_log)" == *"backup OK offsite=FALLA" ]] || fail "backup.log no registró la falla externa: $(last_log)"
ok "credenciales incorrectas: backup local OK, copia externa FALLA y backup.sh sale con error"
# 3. Con el archivo de credenciales correcto (formato con comillas y CRLF tolerados).
printf 'RESTIC_REST_USERNAME=%s\r\nRESTIC_REST_PASSWORD="%s"\r\n' "$user" "$pass" > "$OFFSITE_RESTIC_ENV_FILE"
"$ROOT/infra/scripts/backup.sh" </dev/null
[[ "$(last_log)" == *"backup OK offsite=OK" ]] || fail "backup.log: $(last_log)"
"$ROOT/infra/scripts/backup.sh" --verify-offsite </dev/null
backup_ops snapshots offsite > "$tmp/snaps"
for t in db keyring config; do grep -qw "$t" "$tmp/snaps" || fail "falta una instantánea «$t» en el repositorio externo"; done
ok "copia externa con credenciales: db, keyring y config; el último dump externo es válido e igual al local"
"$ROOT/infra/scripts/backup.sh" </dev/null >/dev/null
backup_ops offsite-verify >/dev/null
ok "segunda copia (incremental) OK"

if (( rotate )); then
  info "Rotación de la contraseña de restic (local + externo)"
  cp -p "$SECRETS_DIR/restic_password" "$tmp/old_password"
  oldm="$tmp/old_password:/run/alvarez/old_password:ro"
  "$ROOT/infra/scripts/rotate-secrets.sh" restic
  [[ -s "$SECRETS_DIR/restic_password.previous" ]] || fail "no quedó restic_password.previous"
  for r in local offsite; do
    backup_ops key-check "$r" /run/secrets/restic_password >/dev/null || fail "la contraseña nueva no abre $r"
    backup_ops -v "$oldm" -- key-check "$r" /run/alvarez/old_password >/dev/null || fail "la anterior dejó de abrir $r antes de confirmar"
  done
  ok "fase 1: la nueva y la anterior abren el repositorio local y el externo"
  "$ROOT/infra/scripts/backup.sh" </dev/null >/dev/null
  [[ "$(last_log)" == *"offsite=OK" ]] || fail "backup con la contraseña nueva: $(last_log)"
  ok "backup y copia externa con la contraseña nueva"
  printf 'GUARDADA\n' | "$ROOT/infra/scripts/rotate-secrets.sh" restic-confirm
  [[ ! -e "$SECRETS_DIR/restic_password.previous" ]] || fail "restic_password.previous sigue después de confirmar"
  for r in local offsite; do
    backup_ops key-check "$r" /run/secrets/restic_password >/dev/null || fail "la contraseña nueva no abre $r"
    if backup_ops -v "$oldm" -- key-check "$r" /run/alvarez/old_password >/dev/null 2>&1; then fail "la anterior todavía abre $r"; fi
  done
  ok "fase 2: solo la contraseña nueva abre el repositorio local y el externo"
  printf 'GUARDADA\n' | "$ROOT/infra/scripts/rotate-secrets.sh" restic-confirm >/dev/null 2>&1 && fail "restic-confirm sin rotación pendiente debería fallar"
  "$ROOT/infra/scripts/backup.sh" </dev/null >/dev/null
  backup_ops offsite-verify >/dev/null
  ok "backup, copia y verificación externa después de la rotación"
fi
echo "Copia externa OK"
