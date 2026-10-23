#!/usr/bin/env bash
# Restauración REAL sobre producción. Nunca automática: pide confirmación explícita.
#   ./infra/scripts/restore.sh <snapshot|latest> [--with-keyring [--keyring-snapshot ID]]
#       Reemplaza la base por la instantánea (antes hace un backup de seguridad del estado actual). Con --with-keyring
#       también reemplaza el key ring: solo si se perdieron la base Y las claves.
#   ./infra/scripts/restore.sh --keyring-only [ID]
#       Volumen de claves perdido o vacío con la base intacta («Key ring rechazado» al arrancar la API): restaura SOLO el
#       key ring y NO toca la base (no se pierde nada de lo cargado después del último backup).
# Sin ID se usa la instantánea del key ring más reciente que tenga claves (las de un /keys vacío se saltean).
source "$(dirname "$0")/lib.sh"
load_env
usage="Uso: restore.sh <snapshot|latest> [--with-keyring [--keyring-snapshot ID]] | restore.sh --keyring-only [ID]"
mode=full; requested=""; with_keys=0; keyreq=latest
case "${1:-}" in
  --keyring-only)
    mode=keys; with_keys=1; keyreq="${2:-latest}"
    (( $# <= 2 )) || die "$usage" ;;
  ""|-*) die "$usage" ;;
  *)
    requested="$1"; shift
    while (( $# > 0 )); do
      case "$1" in
        --with-keyring) with_keys=1 ;;
        --keyring-snapshot) keyreq="${2:?Falta el ID de --keyring-snapshot}"; shift ;;
        *) die "Opción desconocida: $1. $usage" ;;
      esac
      shift
    done
    [[ "$keyreq" == latest || $with_keys -eq 1 ]] || die "--keyring-snapshot va junto con --with-keyring. $usage" ;;
esac
keys_volume="${COMPOSE_PROJECT_NAME:-alvarez-finanzas}_keys"
backup_img="alvarez-finanzas-backup:${IMAGE_TAG:-local}"
prepare_backup_perms
dc --profile ops run --rm -T backup snapshots </dev/null
# Los IDs se fijan ANTES del backup de seguridad: si no, «latest» apuntaría a ese backup nuevo.
if [[ "$mode" == full ]]; then
  snap="$(dc --profile ops run --rm -T backup resolve db "$requested" </dev/null | tr -d '\r')"
  [[ -n "$snap" ]] || die "No se encontró la instantánea $requested"
fi
if (( with_keys )); then
  # La más reciente con claves (o la indicada, si es del key ring y tiene claves). Contiene también las claves viejas,
  # que nunca se borran: sirve para cualquier dump anterior.
  keysnap="$(backup_ops resolve-keyring "$keyreq" | tr -d '\r')"
  [[ -n "$keysnap" ]] || die "No se encontró una instantánea del key ring con claves."
fi

if [[ "$mode" == keys ]]; then
  warn "Se reemplaza el key ring actual (volumen $keys_volume) por el de la instantánea $keysnap. La base NO se toca."
  warn "Si el volumen todavía tiene claves, se guardan antes en $BACKUP_DIR/keys-reemplazadas-*.tar."
  confirm_word CLAVES "Restaurar solo el key ring $keysnap."
else
  warn "Esto REEMPLAZA la base de producción por la instantánea $snap. Los cambios posteriores se pierden."
  confirm_word RESTAURAR "Restaurar $snap sobre producción."
  if (( with_keys )); then
    warn "También se reemplaza el key ring actual por el de la instantánea $keysnap. Solo si se perdieron la base y las claves: si solo se perdió el volumen de claves, cancelá y usá «restore.sh --keyring-only»."
    confirm_word CLAVES "Restaurar key ring."
  fi
fi

work="$(mktemp -d)"; chmod 700 "$work"
trap 'docker run --rm --user 0 -v "$work:/w" --entrypoint sh "$backup_img" -c "rm -rf /w/*"; rm -rf "$work"' EXIT

# Extrae el key ring de la instantánea y reemplaza el del volumen. Lo que hubiera en el volumen se guarda antes en
# BACKUP_DIR (nunca se borra una clave sin copia). La extracción corre como root (--user 0): escribe en el directorio
# temporal del host y conserva dueños y permisos.
restore_keyring() {
  local saved
  info "Restaurando key ring $keysnap"
  dc --profile ops run --rm -T --user 0 -v "$work:/out" backup restore-keys "$keysnap" /out/keyring </dev/null
  compgen -G "$work/keyring/keys/key-*.xml" >/dev/null || die "La instantánea $keysnap no trae claves: no se cambió el key ring."
  mkdir -m 700 "$work/prev"
  docker run --rm --user 0 -v "$work/keyring/keys:/src:ro" -v "$keys_volume:/keys" -v "$work/prev:/prev" \
    --entrypoint sh "$backup_img" \
    -c "cp -a /keys/. /prev/ && rm -f /keys/*.xml && cp -a /src/. /keys/ && chown -R $APP_UID:$APP_UID /keys && chmod 700 /keys"
  if [[ -n "$(ls -A "$work/prev")" ]]; then
    saved="$BACKUP_DIR/keys-reemplazadas-$(date -u +%Y%m%dT%H%M%SZ).tar"
    (umask 077; tar -C "$work/prev" -cf "$saved" .)
    info "Lo que había en el volumen de claves quedó en $saved"
  fi
}

if [[ "$mode" == keys ]]; then
  info "Deteniendo API y web (la base no se toca)"
  dc stop web api </dev/null
  restore_keyring
else
  # El key ring no entra en este backup: puede estar vacío o roto, y no debe pasar a ser el «último» del repositorio.
  info "Backup de seguridad del estado actual (base y configuración)"
  BACKUP_NO_PRUNE=1 BACKUP_LOCAL_ONLY=1 BACKUP_SKIP_KEYRING=1 "$ROOT/infra/scripts/backup.sh" </dev/null
  dc --profile ops run --rm -T --user 0 -v "$work:/out" backup restore-dump "$snap" /out/alvarez.dump </dev/null

  info "Deteniendo API y web"
  dc stop web api </dev/null
  dc exec -T db sh -c 'cat > /tmp/restore.dump' < "$work/alvarez.dump"
  dc exec -T db sh -c 'pg_restore -U alvarez_admin -d alvarez --clean --if-exists --no-owner --no-privileges --role=alvarez_migrator --exit-on-error /tmp/restore.dump; rc=$?; rm -f /tmp/restore.dump; exit $rc' </dev/null
  dc --profile ops run --rm -T migrate </dev/null
  if (( with_keys )); then restore_keyring; fi
fi

dc up -d api </dev/null
wait_healthy api
info "Revocando sesiones"
dc exec -T api dotnet AlvarezFinanzas.Api.dll revoke-sessions </dev/null
dc up -d web </dev/null
wait_healthy web
if [[ "$mode" == keys ]]; then
  info "Key ring $keysnap restaurado sin tocar la base. Ingresá y verificá el acceso; después corré backup.sh."
else
  info "Restauración completada. Verificá el acceso y los datos antes de reabrir el uso normal."
fi
