#!/usr/bin/env bash
# Prueba de la pérdida del volumen de claves sobre una instalación de PRUEBA (detiene la API y vacía el volumen «keys»;
# no usar en producción). Comprueba, de punta a punta:
#   1. con /keys vacío la API no arranca (falla cerrado) y backup.sh termina con error SIN crear una instantánea vacía del
#      key ring (keyring=FALTA en backup.log); resolve-keyring sigue eligiendo la buena;
#   2. «restore.sh --keyring-only» restaura solo las claves: la API vuelve con las claves originales y la base queda
#      intacta (una fila agregada DESPUÉS del último backup sigue ahí; conteos y sumas iguales);
#   3. el backup siguiente vuelve a respaldar el key ring.
# Las claves originales se guardan en un directorio temporal y se reponen si algo falla. Con la API de un release
# anterior al centinela (sin «keyRingSentinel» en verify-data, p. ej. después de un rollback), el paso de la falla
# cerrada se omite: esa API generaría claves nuevas en silencio, así que no se la arranca con el volumen vacío.
#   sudo infra/tests/keyring-restore.sh
source "$(dirname "$0")/../scripts/lib.sh"
load_env
[[ $EUID -eq 0 ]] || die "Correr con sudo (backup.sh y restore.sh ajustan permisos)."
keys_volume="${COMPOSE_PROJECT_NAME:-alvarez-finanzas}_keys"
backup_img="alvarez-finanzas-backup:${IMAGE_TAG:-local}"
schema=alvarez_prueba_claves
tmp="$(mktemp -d)"; chmod 700 "$tmp"
mkdir -m 700 "$tmp/keys"
done_ok=0
ok() { echo "OK    $*"; }
fail() { echo "FALLA $*" >&2; exit 1; }
psql_admin() { dc exec -T db psql -X -q -t -A -v ON_ERROR_STOP=1 -U alvarez_admin -d alvarez "$@" </dev/null; }
api_cli() { dc exec -T api dotnet AlvarezFinanzas.Api.dll "$@" </dev/null; }
key_names() { docker run --rm --user 0 -v "$keys_volume:/keys:ro" --entrypoint sh "$backup_img" -c 'cd /keys && ls -1 key-*.xml 2>/dev/null || true'; }
cleanup() {
  if (( ! done_ok )) && compgen -G "$tmp/keys/key-*.xml" >/dev/null; then
    echo "Reponiendo las claves originales" >&2
    dc stop web api >/dev/null 2>&1 || true
    docker run --rm --user 0 -v "$tmp/keys:/src:ro" -v "$keys_volume:/keys" --entrypoint sh "$backup_img" \
      -c "rm -f /keys/*.xml; cp -a /src/. /keys/ && chown -R $APP_UID:$APP_UID /keys && chmod 700 /keys" || true
    dc up -d api web >/dev/null 2>&1 || true
  fi
  psql_admin -c "DROP SCHEMA IF EXISTS $schema CASCADE" >/dev/null 2>&1 || true
  rm -rf "$tmp"
}
trap cleanup EXIT

wait_healthy api; wait_healthy web
docker run --rm --user 0 -v "$keys_volume:/keys:ro" -v "$tmp/keys:/dst" --entrypoint sh "$backup_img" -c 'cp -a /keys/. /dst/'
compgen -G "$tmp/keys/key-*.xml" >/dev/null || fail "el volumen de claves ya está vacío: la prueba necesita una instalación sana"
orig_keys="$(key_names)"
# Una tabla que SÍ está en el backup (del rol migrador, como el resto) y una fila agregada después: un restore completo
# la perdería; --keyring-only no.
psql_admin -c "DROP SCHEMA IF EXISTS $schema CASCADE" -c "CREATE SCHEMA $schema AUTHORIZATION alvarez_migrator" \
  -c "CREATE TABLE $schema.marca(t text)" -c "ALTER TABLE $schema.marca OWNER TO alvarez_migrator" \
  -c "INSERT INTO $schema.marca VALUES ('antes del backup')" >/dev/null
"$ROOT/infra/scripts/backup.sh" </dev/null >"$tmp/backup0.log" 2>&1 || { tail -30 "$tmp/backup0.log"; fail "backup inicial"; }
good="$(backup_ops resolve-keyring | tr -d '\r')"
[[ -n "$good" ]] || fail "no hay una instantánea del key ring con claves"
psql_admin -c "INSERT INTO $schema.marca VALUES ('después del backup')" >/dev/null
api_cli verify-data > "$tmp/before.json" || fail "verify-data antes de la prueba"
guard=0; grep -q '"keyRingSentinel"' "$tmp/before.json" && guard=1
ok "estado inicial: claves $(wc -l <<<"$orig_keys"), key ring respaldado en $good y una fila posterior al backup"

info "Se pierde el volumen de claves"
dc stop web api </dev/null
docker run --rm --user 0 -v "$keys_volume:/keys" --entrypoint sh "$backup_img" -c 'rm -f /keys/*'
[[ -z "$(key_names)" ]] || fail "no se vació el volumen de claves"
if (( guard )); then
  since="$(date -u +%FT%TZ)"
  dc up -d --no-deps api </dev/null >/dev/null 2>&1 || true
  refused=0
  for _ in $(seq 1 30); do
    if dc logs --no-color --since "$since" api 2>/dev/null | grep -q 'Key ring rechazado'; then refused=1; break; fi
    sleep 2
  done
  (( refused )) || { dc logs --tail 30 api >&2; fail "la API no rechazó el key ring vacío"; }
  dc stop api </dev/null >/dev/null 2>&1
  [[ -z "$(key_names)" ]] || fail "la API escribió claves nuevas en el volumen vacío"
  ok "con /keys vacío la API no arranca («Key ring rechazado») y no escribe claves"
else
  echo "OMITIDO la API desplegada es anterior al centinela: no se la arranca con el volumen vacío"
fi

if "$ROOT/infra/scripts/backup.sh" </dev/null >"$tmp/backup1.log" 2>&1; then tail -20 "$tmp/backup1.log"; fail "backup.sh terminó bien con el volumen de claves vacío"; fi
grep -q 'restore.sh --keyring-only' "$tmp/backup1.log" || { tail -20 "$tmp/backup1.log"; fail "backup.sh no indicó cómo recuperar"; }
[[ "$(tail -1 "$BACKUP_DIR/backup.log")" == *" backup OK offsite="*" keyring=FALTA" ]] || fail "backup.log: $(tail -1 "$BACKUP_DIR/backup.log")"
[[ "$(backup_ops resolve-keyring | tr -d '\r')" == "$good" ]] || fail "resolve-keyring dejó de elegir $good"
ok "backup.sh con /keys vacío: base respaldada, key ring no, sale con error y resolve-keyring sigue en $good"

info "restore.sh --keyring-only"
printf 'CLAVES\n' | "$ROOT/infra/scripts/restore.sh" --keyring-only >"$tmp/restore.log" 2>&1 || { tail -40 "$tmp/restore.log"; fail "restore.sh --keyring-only"; }
grep -q "Key ring $good restaurado sin tocar la base" "$tmp/restore.log" || { tail -20 "$tmp/restore.log"; fail "restore.sh no restauró $good"; }
grep -q 'keys-reemplazadas' "$tmp/restore.log" && fail "con el volumen vacío no debería guardar claves reemplazadas"
[[ "$(key_names)" == "$orig_keys" ]] || fail "las claves restauradas no son las originales"
wait_healthy api; wait_healthy web
[[ "$(psql_admin -c "SELECT count(*) FROM $schema.marca WHERE t = 'después del backup'")" == 1 ]] \
  || fail "se perdió la fila agregada después del backup: la base no quedó intacta"
api_cli verify-data > "$tmp/after.json" || { cat "$tmp/after.json"; fail "verify-data después de restaurar las claves"; }
python3 - "$tmp/before.json" "$tmp/after.json" <<'PY' || fail "la base cambió al restaurar solo las claves"
import json, sys
b, a = (json.load(open(p)) for p in sys.argv[1:3])
for d in (b, a): d["counts"].pop("auditEvents", None)   # revoke-sessions audita
diff = [k for k in ("counts", "entrySums", "planSums", "totpSecrets", "totpDecryptable") if b.get(k) != a.get(k)]
assert a.get("keyRingSentinel", "Verified") == "Verified", a["keyRingSentinel"]
assert a["totpDecryptable"] == a["totpSecrets"], "TOTP sin descifrar"
sys.exit(1 if diff else 0)
PY
ok "--keyring-only: la API volvió con las claves originales ($good), secretos TOTP descifrables, base intacta"

psql_admin -c "DROP SCHEMA $schema CASCADE" >/dev/null
"$ROOT/infra/scripts/backup.sh" </dev/null >"$tmp/backup2.log" 2>&1 || { tail -20 "$tmp/backup2.log"; fail "backup después de restaurar las claves"; }
[[ "$(tail -1 "$BACKUP_DIR/backup.log")" == *" keyring=OK" ]] || fail "backup.log: $(tail -1 "$BACKUP_DIR/backup.log")"
ok "el backup siguiente vuelve a respaldar el key ring"
done_ok=1
echo "Pérdida del volumen de claves: OK"
