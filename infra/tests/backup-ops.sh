#!/usr/bin/env bash
# Prueba hermética de infra/docker/backup-entrypoint.sh y backup-ops.sh (los del checkout) dentro de la imagen de backup:
# sin red, sin la instalación y sin secretos reales. Una PostgreSQL de un solo uso en el mismo contenedor, repositorio
# local y «externo» en /tmp (tmpfs). Comprueba:
#   - un /keys sin claves no genera una instantánea «keyring» (código 3) y la base sí queda respaldada;
#   - resolve-keyring saltea instantáneas de un /keys vacío y valida un ID explícito;
#   - offsite-verify con un dump de más de 64 KiB (antes fallaba con SIGPIPE, código 141);
#   - key-remove-for: un repositorio bloqueado NO pasa por «nada que quitar»; con OFFSITE_PRUNE=0 no borra en el externo.
#   infra/tests/backup-ops.sh [IMAGEN_BACKUP]      (por defecto alvarez-finanzas-backup:local)
set -euo pipefail
cd "$(dirname "$0")/../.."
img="${1:-alvarez-finanzas-backup:local}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
# Valores de prueba (no son secretos): el contenedor corre como 1654 y tiene que poder leerlos.
printf 'contrasena-de-prueba-restic' > "$tmp/restic_password"
printf 'sin-uso-auth-trust' > "$tmp/db_migrator_password"
chmod 644 "$tmp/restic_password" "$tmp/db_migrator_password"
cat > "$tmp/inner.sh" <<'INNER'
set -eu
# shellcheck disable=SC3040
set -o pipefail
ok() { echo "OK    $*"; }
fail() { echo "FALLA: $*" >&2; exit 1; }
ops() { sh /opt/alvarez/backup-ops.sh "$@"; }
entry() { sh /opt/alvarez/backup-entrypoint.sh "$@"; }
export RESTIC_REPOSITORY=/tmp/local RESTIC_CACHE_DIR=/tmp/cache BACKUP_HOST_TAG=alvarez-finanzas
export RESTIC_PASSWORD_FILE=/run/secrets/restic_password
keyring_ids() { RESTIC_REPOSITORY="${1:-/tmp/local}" restic snapshots --host alvarez-finanzas --tag keyring --json | grep -o '"short_id":"[^"]*"' | cut -d'"' -f4 || true; }

# PostgreSQL de un solo uso («db» apunta a 127.0.0.1) con ~320 KiB de datos aleatorios: el dump no se comprime.
initdb -D /tmp/pg -U alvarez_migrator --auth=trust >/tmp/initdb.log 2>&1 || { cat /tmp/initdb.log; fail "initdb"; }
pg_ctl -D /tmp/pg -o "-c listen_addresses=127.0.0.1 -c unix_socket_directories=/tmp" -l /tmp/pg.log -w start >/dev/null
createdb -h 127.0.0.1 -U alvarez_migrator alvarez
psql -h 127.0.0.1 -U alvarez_migrator -d alvarez -q -v ON_ERROR_STOP=1 -c "CREATE TABLE relleno(b bytea)" \
  -c "INSERT INTO relleno SELECT decode(string_agg(lpad(to_hex(floor(random()*4294967296)::bigint), 8, '0'), ''), 'hex') FROM generate_series(1, 80000)"

entry init >/dev/null
# 1. /keys vacío: la base se respalda, el key ring no, y el código es 3.
rc=0; entry backup >/tmp/out 2>&1 || rc=$?
[ "$rc" = 3 ] || { cat /tmp/out; fail "backup con /keys vacío salió con $rc (se esperaba 3)"; }
[ -z "$(keyring_ids)" ] || fail "backup con /keys vacío creó una instantánea del key ring"
restic snapshots --host alvarez-finanzas --tag db --json | grep -q '"short_id"' || fail "backup con /keys vacío no respaldó la base"
ok "/keys vacío: base respaldada, sin instantánea del key ring, código 3"
rc=0; ops resolve-keyring >/tmp/out 2>&1 || rc=$?
[ "$rc" != 0 ] && grep -q 'No hay ninguna instantánea del key ring con claves' /tmp/out || { cat /tmp/out; fail "resolve-keyring sin instantáneas"; }
ok "resolve-keyring sin instantáneas con claves falla con un mensaje claro"

# 2. Con claves: instantánea normal.
echo '<key id="1"/>' > /keys/key-11111111-1111-1111-1111-111111111111.xml
entry backup >/tmp/out 2>&1 || { cat /tmp/out; fail "backup con claves falló"; }
good="$(keyring_ids | tail -1)"
[ -n "$good" ] || fail "backup con claves no creó la instantánea del key ring"
# BACKUP_SKIP_KEYRING=1 (backup de seguridad de restore.sh): no agrega instantáneas del key ring.
BACKUP_SKIP_KEYRING=1 BACKUP_NO_PRUNE=1 entry backup >/tmp/out 2>&1 || { cat /tmp/out; fail "backup con BACKUP_SKIP_KEYRING=1 falló"; }
[ "$(keyring_ids | tail -1)" = "$good" ] || fail "BACKUP_SKIP_KEYRING=1 creó una instantánea del key ring"
ok "con claves: instantánea $good; BACKUP_SKIP_KEYRING=1 no agrega ninguna"

# 3. El volumen se pierde: el backup siguiente no tapa la buena; una instantánea vacía (versión anterior) se saltea.
rm -f /keys/*.xml
rc=0; entry backup >/tmp/out 2>&1 || rc=$?
[ "$rc" = 3 ] && [ "$(keyring_ids | tail -1)" = "$good" ] || { cat /tmp/out; fail "un /keys vacío pasó a ser la última instantánea del key ring"; }
restic backup -q --host alvarez-finanzas --tag keyring /keys
empty="$(keyring_ids | tail -1)"
[ "$empty" != "$good" ] || fail "no se creó la instantánea vacía de prueba"
[ "$(ops resolve-keyring)" = "$good" ] || fail "resolve-keyring no eligió la última instantánea con claves"
[ "$(ops resolve-keyring latest)" = "$good" ] || fail "resolve-keyring latest"
[ "$(ops resolve-keyring "$good")" = "$good" ] || fail "resolve-keyring con el ID de la buena"
rc=0; ops resolve-keyring "$empty" >/tmp/out 2>&1 || rc=$?
[ "$rc" != 0 ] || fail "resolve-keyring aceptó una instantánea sin claves"
dbid="$(restic snapshots --host alvarez-finanzas --tag db --latest 1 --json | grep -o '"short_id":"[^"]*"' | cut -d'"' -f4)"
rc=0; ops resolve-keyring "$dbid" >/tmp/out 2>&1 || rc=$?
[ "$rc" != 0 ] && grep -q 'no es una instantánea del key ring' /tmp/out || { cat /tmp/out; fail "resolve-keyring aceptó una instantánea de la base"; }
rc=0; ops resolve-keyring 'zz;rm' >/tmp/out 2>&1 || rc=$?
[ "$rc" != 0 ] || fail "resolve-keyring aceptó un ID no hexadecimal"
ok "resolve-keyring: saltea la vacía ($empty), elige $good y rechaza IDs de otra etiqueta, sin claves o inválidos"

# 4. Copia externa y verificación con un dump de más de 64 KiB.
export OFFSITE_RESTIC_REPOSITORY=/tmp/off
size="$(restic dump --host alvarez-finanzas --tag db latest alvarez.dump | wc -c)"
[ "$size" -gt 131072 ] || fail "el dump de prueba es chico ($size bytes): no cubre el caso de más de 64 KiB"
ops offsite-copy >/tmp/out 2>&1 || { cat /tmp/out; fail "offsite-copy"; }
ops offsite-verify >/tmp/out 2>&1 || { cat /tmp/out; fail "offsite-verify con un dump de $size bytes"; }
grep -q 'Copia externa verificada' /tmp/out || { cat /tmp/out; fail "offsite-verify no confirmó"; }
ok "offsite-verify con un dump de $size bytes"
# Un dump externo distinto del local se detecta.
psql -h 127.0.0.1 -U alvarez_migrator -d alvarez -q -c "INSERT INTO relleno VALUES ('\\x00')"
BACKUP_NO_PRUNE=1 entry backup >/dev/null 2>&1 || true
rc=0; ops offsite-verify >/tmp/out 2>&1 || rc=$?
[ "$rc" != 0 ] && grep -q 'no coincide con el local' /tmp/out || { cat /tmp/out; fail "offsite-verify no detectó un dump externo desactualizado"; }
ok "offsite-verify detecta un dump externo distinto del local"

# 5. Claves: agregar, identificar, contraseña que no abre.
printf 'otra-contrasena' > /tmp/pw2; printf 'no-abre-nada' > /tmp/wrong
ops key-add local /tmp/pw2 >/dev/null 2>&1 || fail "key-add local"
[ -n "$(ops key-id local /tmp/pw2)" ] || fail "key-id no encontró la clave agregada"
[ -z "$(ops key-id local /tmp/wrong)" ] || fail "key-id devolvió algo para una contraseña que no abre"
ops key-remove-for local /tmp/wrong >/tmp/out 2>&1 || { cat /tmp/out; fail "key-remove-for con una contraseña que no abre"; }
grep -q 'nada que quitar' /tmp/out || fail "key-remove-for con una contraseña que no abre debería decir «nada que quitar»"
ok "key-add, key-id y «nada que quitar» solo cuando la contraseña no abre el repositorio"

# 6. Repositorio con un bloqueo exclusivo (restic check detenido con SIGSTOP): no es «nada que quitar».
dd if=/dev/urandom of=/tmp/grande bs=1M count=24 2>/dev/null
restic backup -q --host otro --tag relleno /tmp/grande; rm -f /tmp/grande
held=0
for _ in 1 2 3 4 5; do
  restic check --read-data >/dev/null 2>&1 & chk=$!
  n=0; while [ -z "$(ls /tmp/local/locks)" ] && [ $n -lt 5000 ]; do n=$((n + 1)); done
  kill -STOP "$chk" 2>/dev/null || true
  if [ -n "$(ls /tmp/local/locks)" ] && kill -0 "$chk" 2>/dev/null; then held=1; break; fi
  kill -CONT "$chk" 2>/dev/null || true; wait "$chk" || true
done
[ "$held" = 1 ] || fail "no se pudo sostener un bloqueo exclusivo para la prueba"
rc=0; RESTIC_RETRY_LOCK=1s ops key-remove-for local /tmp/pw2 >/tmp/out 2>&1 || rc=$?
kill -CONT "$chk"; wait "$chk" || true
[ "$rc" != 0 ] || { cat /tmp/out; fail "key-remove-for terminó bien con el repositorio bloqueado"; }
grep -q 'nada que quitar' /tmp/out && { cat /tmp/out; fail "un repositorio bloqueado pasó por «nada que quitar»"; }
[ -n "$(ops key-id local /tmp/pw2)" ] || fail "la clave desapareció con el repositorio bloqueado"
ops key-remove-for local /tmp/pw2 >/tmp/out 2>&1 || { cat /tmp/out; fail "key-remove-for sin bloqueo"; }
[ -z "$(ops key-id local /tmp/pw2)" ] || fail "key-remove-for no quitó la clave"
ok "repositorio bloqueado: key-remove-for falla (código $rc) en vez de «nada que quitar»; sin bloqueo la quita"

# 7. Externo de solo agregar (OFFSITE_PRUNE=0): no se intenta borrar la clave allá (código 3) y sigue listada.
ops key-add offsite /tmp/pw2 >/dev/null 2>&1 || fail "key-add offsite"
id="$(ops key-id offsite /tmp/pw2)"
rc=0; OFFSITE_PRUNE=0 ops key-remove-for offsite /tmp/pw2 >/tmp/out 2>&1 || rc=$?
[ "$rc" = 3 ] || { cat /tmp/out; fail "key-remove-for en un externo de solo agregar salió con $rc (se esperaba 3)"; }
ops key-ids offsite | grep -qx "$id" || fail "key-ids no lista la clave que quedó en el externo"
OFFSITE_PRUNE=1 ops key-remove-for offsite /tmp/pw2 >/dev/null 2>&1 || fail "key-remove-for offsite con poda"
ops key-ids offsite | grep -qx "$id" && fail "la clave sigue en el externo después de quitarla"
ok "externo de solo agregar: código 3 sin borrar; con OFFSITE_PRUNE=1 se quita"

pg_ctl -D /tmp/pg -m fast stop >/dev/null
echo "backup-ops OK"
INNER
docker run --rm --network none --add-host db:127.0.0.1 --tmpfs /tmp:size=320m \
  --tmpfs /keys:uid=1654,gid=1654,mode=0700 \
  -v "$tmp/inner.sh:/opt/alvarez/inner.sh:ro" \
  -v "$PWD/infra/docker/backup-ops.sh:/opt/alvarez/backup-ops.sh:ro" \
  -v "$PWD/infra/docker/backup-entrypoint.sh:/opt/alvarez/backup-entrypoint.sh:ro" \
  -v "$tmp/restic_password:/run/secrets/restic_password:ro" \
  -v "$tmp/db_migrator_password:/run/secrets/db_migrator_password:ro" \
  --entrypoint sh "$img" /opt/alvarez/inner.sh
