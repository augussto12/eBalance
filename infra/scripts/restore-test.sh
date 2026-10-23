#!/usr/bin/env bash
# Prueba de restauración en un entorno AISLADO (red interna sin salida, sin puertos): no toca producción.
# Restaura la última instantánea (o la indicada), restaura el key ring, verifica conteos, sumas por moneda,
# consistencia de cuotas, descifrado de secretos TOTP con las claves restauradas y arranque de la API;
# revoca las sesiones restauradas. Registra fecha/duración/resultado sin datos sensibles.
#   ./infra/scripts/restore-test.sh [snapshot]
source "$(dirname "$0")/lib.sh"
load_env
requested="${1:-latest}"
snap="$requested"
started=$(date +%s)
proj="alvarez-restoretest-$$"
net="$proj-net"
work="$(mktemp -d)"
chmod 700 "$work"
api_img="alvarez-finanzas-api:${IMAGE_TAG:-local}"
backup_img="alvarez-finanzas-backup:${IMAGE_TAG:-local}"
pg_img="postgres:18.6-alpine3.24"
result=FALLA

cleanup() {
  docker rm -f "$proj-db" "$proj-api" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
  docker run --rm --user 0 -v "$work:/w" --entrypoint sh "$backup_img" -c 'rm -rf /w/*' >/dev/null 2>&1 || true
  rm -rf "$work"
  echo "$(date -u +%FT%TZ) restore-test snapshot=$snap resultado=$result duracion=$(( $(date +%s) - started ))s" | tee -a "$BACKUP_DIR/restore-tests.log"
}
trap cleanup EXIT
prepare_backup_perms

snap="$(dc --profile ops run --rm -T backup resolve db "$requested" </dev/null | tr -d '\r')"
[[ -n "$snap" ]] || die "No se encontró la instantánea $requested"
# Igual que restore.sh: el key ring más reciente que tenga claves (una instantánea de /keys vacío no sirve).
keysnap="$(backup_ops resolve-keyring | tr -d '\r')"
[[ -n "$keysnap" ]] || die "No hay ninguna instantánea del key ring con claves"
info "Extrayendo instantánea $snap (key ring $keysnap)"
# Extracción como root (--user 0): escribe en este directorio temporal y conserva dueños y permisos del key ring.
dc --profile ops run --rm -T --user 0 -v "$work:/out" backup restore-dump "$snap" /out/alvarez.dump
dc --profile ops run --rm -T --user 0 -v "$work:/out" backup restore-keys "$keysnap" /out/keyring
[[ -s "$work/alvarez.dump" ]] || die "El dump restaurado está vacío"
docker run --rm --user 0 -v "$work:/w" --entrypoint sh "$backup_img" -c "chown -R $APP_UID:$APP_UID /w/keyring && chmod -R go-rwx /w/keyring"

info "Base aislada"
docker network create --internal "$net" >/dev/null
pw="$(openssl rand -hex 24)"
printf '%s' "$pw" > "$work/pw"
docker run --rm --user 0 -v "$work:/w" --entrypoint sh "$backup_img" -c "chown $APP_UID:$APP_UID /w/pw && chmod 400 /w/pw"
docker run -d --name "$proj-db" --network "$net" -e POSTGRES_USER=restore -e POSTGRES_DB=alvarez -e POSTGRES_PASSWORD="$pw" "$pg_img" >/dev/null
for _ in $(seq 1 30); do docker exec "$proj-db" pg_isready -U restore -d alvarez -q && break; sleep 1; done
sleep 2
docker run --rm --network "$net" -v "$work:/w:ro" -e PGPASSWORD="$pw" "$pg_img" \
  pg_restore -h "$proj-db" -U restore -d alvarez --no-owner --no-privileges --exit-on-error /w/alvarez.dump
# Igual que restore.sh: migraciones pendientes sobre la copia. Un backup previo a un deploy tiene el esquema anterior y la
# API nueva lo necesita (p. ej. la tabla del centinela del key ring). Sin GRANT: los roles de producción no existen acá.
info "Migraciones pendientes sobre la copia"
docker run --rm --network "$net" --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true \
  -v "$work/pw:/run/secrets/pw:ro" -e App__SkipGrants=true -e ConnectionStrings__MigratorPasswordFile=/run/secrets/pw \
  -e "ConnectionStrings__Migrator=Host=$proj-db;Database=alvarez;Username=restore;Gss Encryption Mode=Disable" \
  "$api_img" migrate

info "API aislada con el key ring restaurado"
common=(--network "$net" --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true
  -v "$work/keyring/keys:/keys" -v "$SECRETS_DIR/keyring:/run/keyring:ro" -v "$work/pw:/run/secrets/pw:ro"
  -e ASPNETCORE_ENVIRONMENT=Production
  -e "ConnectionStrings__App=Host=$proj-db;Database=alvarez;Username=restore;Gss Encryption Mode=Disable" -e ConnectionStrings__AppPasswordFile=/run/secrets/pw
  -e App__PublicOrigin=https://restore-test.invalid -e App__TrustedProxies=127.0.0.1 -e App__DataProtectionApplicationName=alvarez-finanzas-production
  -e App__KeyRingPath=/keys -e App__KeyRingCertificatePath=/run/keyring/current.pfx -e App__KeyRingCertificatePasswordFile=/run/keyring/current.password)
# El informe (conteos y sumas) queda en el directorio temporal: por pantalla y en los registros, solo un resumen sin montos.
summary() {
  python3 - "$work/restored.json" <<'PY' || true
import json, sys
r = json.load(open(sys.argv[1]))
print(f"verify-data: cuotas inconsistentes={r['plansWithInconsistentInstallments']} "
      f"TOTP descifrables={r['totpDecryptable']}/{r['totpSecrets']} sesiones revocadas={r['sessionsRevoked']}")
PY
}
if ! docker run --rm "${common[@]}" "$api_img" verify-data --revoke-sessions > "$work/restored.json"; then
  summary
  die "La verificación de datos restaurados falló"
fi
summary
docker run -d --name "$proj-api" "${common[@]}" "$api_img" >/dev/null
for _ in $(seq 1 30); do
  docker exec "$proj-api" wget -q -O - http://127.0.0.1:8080/api/health/ready 2>/dev/null | grep -q ok && break
  sleep 1
done
docker exec "$proj-api" wget -q -O - http://127.0.0.1:8080/api/health/ready | grep -q ok || die "La API restaurada no quedó lista"
docker exec "$proj-api" wget -q -O - --header "X-Forwarded-Proto: https" http://127.0.0.1:8080/api/v1/auth/csrf | grep -q token || { docker logs --tail 40 "$proj-api" >&2; die "Data Protection no funciona con las claves restauradas"; }
code=$(docker exec "$proj-api" sh -c 'wget -S -q -O /dev/null http://127.0.0.1:8080/api/v1/entries 2>&1 | grep -o "HTTP/1.1 [0-9]*" | tail -1' || true)
[[ "$code" == *401* ]] || die "Endpoint privado sin sesión no devolvió 401 ($code)"

if [[ -n "$(dc ps -q api 2>/dev/null)" ]]; then
  info "Comparando con producción (cambios posteriores al backup generan diferencias esperables)"
  dc exec -T api dotnet AlvarezFinanzas.Api.dll verify-data > "$work/live.json" || true
  python3 - "$work/restored.json" "$work/live.json" <<'PY' || warn "Diferencias con producción: revisá si hubo cambios después del backup."
import json, sys
r, l = (json.load(open(p)) for p in sys.argv[1:3])
keys = ["counts", "entrySums", "planSums"]
diff = [k for k in keys if r.get(k) != l.get(k)]
print("Coincide con producción" if not diff else f"Difiere en: {', '.join(diff)}")
sys.exit(1 if diff else 0)
PY
fi
result=OK
info "Restore-test OK"
