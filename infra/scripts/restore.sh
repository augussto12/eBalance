#!/usr/bin/env bash
# Restauración REAL sobre producción. Nunca automática: pide confirmación explícita y hace un backup previo.
#   ./infra/scripts/restore.sh <snapshot|latest> [--with-keyring]
source "$(dirname "$0")/lib.sh"
load_env
requested="${1:?Indicá la instantánea (restic snapshots) o latest}"
with_keys="${2:-}"
dc --profile ops run --rm -T backup snapshots </dev/null
# El key ring más reciente contiene también las claves viejas (no se borran): se usa siempre el último.
# Se fija el ID ANTES del backup de seguridad: si no, «latest» apuntaría a ese backup nuevo.
snap="$(dc --profile ops run --rm -T backup resolve db "$requested" </dev/null | tr -d '\r')"
keysnap="$(dc --profile ops run --rm -T backup resolve keyring latest </dev/null | tr -d '\r')"
[[ -n "$snap" ]] || die "No se encontró la instantánea $requested"
warn "Esto REEMPLAZA la base de producción por la instantánea $snap. Los cambios posteriores se pierden."
confirm_word RESTAURAR "Restaurar $snap sobre producción."
if [[ "$with_keys" == --with-keyring ]]; then
  warn "También se reemplaza el key ring actual por el de la instantánea $keysnap (solo si se perdió el volumen de claves)."
  confirm_word CLAVES "Restaurar key ring."
fi

info "Backup de seguridad del estado actual"
BACKUP_NO_PRUNE=1 "$ROOT/infra/scripts/backup.sh" </dev/null
work="$(mktemp -d)"; chmod 700 "$work"
trap 'docker run --rm -v "$work:/w" --entrypoint sh alvarez-finanzas-backup:${IMAGE_TAG:-local} -c "rm -rf /w/*"; rm -rf "$work"' EXIT
dc --profile ops run --rm -T -v "$work:/out" backup restore-dump "$snap" /out/alvarez.dump </dev/null

info "Deteniendo API y web"
dc stop web api </dev/null
dc exec -T db sh -c 'cat > /tmp/restore.dump' < "$work/alvarez.dump"
dc exec -T db sh -c 'pg_restore -U alvarez_admin -d alvarez --clean --if-exists --no-owner --no-privileges --role=alvarez_migrator --exit-on-error /tmp/restore.dump; rc=$?; rm -f /tmp/restore.dump; exit $rc' </dev/null
dc --profile ops run --rm -T migrate </dev/null

if [[ "$with_keys" == --with-keyring ]]; then
  info "Restaurando key ring $keysnap"
  dc --profile ops run --rm -T -v "$work:/out" backup restore-keys "$keysnap" /out/keyring </dev/null
  docker run --rm -v "$work/keyring/keys:/src:ro" -v "${COMPOSE_PROJECT_NAME:-alvarez-finanzas}_keys:/keys" --entrypoint sh "alvarez-finanzas-backup:${IMAGE_TAG:-local}" \
    -c "rm -f /keys/*.xml; cp -a /src/. /keys/ && chown -R $APP_UID:$APP_UID /keys && chmod 700 /keys"
fi

dc up -d api
info "Revocando sesiones restauradas"
dc exec -T api dotnet AlvarezFinanzas.Api.dll revoke-sessions </dev/null
dc up -d web
info "Restauración completada. Verificá el acceso y los datos antes de reabrir el uso normal."
