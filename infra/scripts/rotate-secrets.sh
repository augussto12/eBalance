#!/usr/bin/env bash
# Rotación de secretos sin perder acceso. Acepta varios objetivos en una ejecución (se validan todos antes de empezar):
#   ./infra/scripts/rotate-secrets.sh db-app db-migrator keyring-cert restic
#   db-app | db-migrator   nueva contraseña del rol; psql la lee del archivo montado (no va en argumentos ni en SQL armado)
#   keyring-cert           nuevo certificado; el anterior queda como previous-*.pfx para descifrar claves viejas. Guardá
#                          fuera del servidor el nuevo Y los previous-*: no se borran nunca (RUNBOOK §7)
#   restic                 agrega una clave nueva al repositorio local Y al externo (si hay). La anterior sigue válida y
#                          queda en restic_password.previous hasta confirmar: nunca se pierde el acceso a los backups.
#   restic-confirm         DESPUÉS de guardar la contraseña nueva fuera del servidor: quita la anterior de los repositorios.
#                          Con un destino externo de solo agregar (OFFSITE_PRUNE=0) la clave anterior se quita allá desde
#                          otra máquina: queda anotada en SECRETS_DIR/restic-offsite-key-pending y, una vez quitada,
#                          «restic-confirm» de nuevo lo comprueba y borra la nota.
# La API se reinicia una sola vez al final si hace falta (db-app, keyring-cert).
source "$(dirname "$0")/lib.sh"
load_env
usage="Uso: rotate-secrets.sh db-app|db-migrator|keyring-cert|restic|restic-confirm [...]"
(( $# > 0 )) || die "$usage"
declare -A seen=()
for what in "$@"; do
  case "$what" in db-app|db-migrator|keyring-cert|restic|restic-confirm) ;; *) die "Opción desconocida: $what. $usage";; esac
  [[ -z "${seen[$what]:-}" ]] || die "$what repetido"
  seen[$what]=1
done
[[ -z "${seen[restic]:-}" || -z "${seen[restic-confirm]:-}" ]] \
  || die "restic-confirm va en una ejecución aparte, después de guardar la contraseña nueva fuera del servidor."
umask 077
newpw() { openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-48; }

rotate_db_role() {
  local role="$1" name="$2" file="$SECRETS_DIR/$2"
  [[ -s "$file" ]] || die "Falta $file"
  cp -p "$file" "$file.old"
  printf '%s' "$(newpw)" > "$file.tmp"
  cat "$file.tmp" > "$file"; rm -f "$file.tmp"   # mismo inodo: el contenedor db (bind mount) ve el cambio
  # :'pw' es un literal citado por psql; el valor sale del archivo de secretos montado en el contenedor.
  if ! dc exec -T db psql -X -q -v ON_ERROR_STOP=1 -U alvarez_admin -d alvarez -v role="$role" -v pwfile="/run/secrets/$name" <<'SQL'
\set pw `cat :'pwfile'`
ALTER ROLE :"role" PASSWORD :'pw';
SQL
  then
    cat "$file.old" > "$file"; rm -f "$file.old"
    die "No se pudo cambiar la contraseña de $role: quedó la anterior."
  fi
  rm -f "$file.old"
  info "Contraseña de $role rotada."
}

rotate_keyring_cert() {
  local dir="$SECRETS_DIR/keyring" stamp
  stamp="$(date -u +%Y%m%d%H%M%S)"
  cp -p "$dir/current.pfx" "$dir/previous-$stamp.pfx"; cp -p "$dir/current.password" "$dir/previous-$stamp.password"
  rm -f "$dir/current.pfx" "$dir/current.password"
  "$ROOT/infra/scripts/bootstrap-secrets.sh" >/dev/null
  warn "Guardá fuera del servidor el certificado NUEVO (current.pfx y current.password) y también previous-$stamp.pfx y previous-$stamp.password. No los borres nunca: el key ring conserva las claves cifradas con cada certificado, y el centinela, los secretos TOTP y las sesiones de los backups anteriores dependen de ellas. Sin todos los certificados, la API puede no arrancar con un backup restaurado."
}

restic_repos() { echo local; if offsite_configured; then echo offsite; fi; }

rotate_restic() {
  local cur="$SECRETS_DIR/restic_password" new="$SECRETS_DIR/restic_password.new" prev="$SECRETS_DIR/restic_password.previous"
  local repos r added=() newmount
  mapfile -t repos < <(restic_repos)
  [[ ! -e "$prev" ]] || die "Hay una rotación de restic sin confirmar ($prev): terminala con «rotate-secrets.sh restic-confirm»."
  prepare_backup_perms
  # 0. Cada repositorio abre con la contraseña vigente: si el externo no responde, no se cambia nada.
  for r in "${repos[@]}"; do
    backup_ops key-check "$r" /run/secrets/restic_password || die "El repositorio $r no responde o no abre: no se cambió nada."
  done
  printf '%s' "$(newpw)" > "$new"
  chown "0:$BACKUP_UID" "$new"; chmod 440 "$new"
  newmount="$(realpath "$new"):/run/alvarez/new_password:ro"
  # 1. Se AGREGA la clave nueva a cada repositorio (la anterior sigue válida). Si uno falla, se deshace lo hecho.
  for r in "${repos[@]}"; do
    if backup_ops -v "$newmount" -- key-add "$r" /run/alvarez/new_password; then
      added+=("$r")
    else
      for a in "${added[@]}"; do backup_ops -v "$newmount" -- key-remove-for "$a" /run/alvarez/new_password || true; done
      rm -f "$new"
      die "No se pudo agregar la clave nueva al repositorio $r: la contraseña vigente no cambió."
    fi
  done
  # 2. La contraseña nueva abre todos los repositorios.
  for r in "${repos[@]}"; do backup_ops -v "$newmount" -- key-check "$r" /run/alvarez/new_password; done
  # 3. Pasa a ser la vigente; la anterior queda en restic_password.previous (también abre todo) hasta restic-confirm.
  cp -p "$cur" "$prev"; touch "$prev"   # la fecha marca el inicio de la rotación (maintenance.sh avisa a los 7 días)
  cat "$new" > "$cur"; rm -f "$new"
  info "Clave nueva de restic agregada a: ${repos[*]}. La anterior sigue válida y quedó en $prev."
  warn "Guardá la contraseña NUEVA ($cur) en tu gestor de contraseñas, fuera del servidor. Después corré: ./infra/scripts/rotate-secrets.sh restic-confirm"
}

# Destino externo de solo agregar (OFFSITE_PRUNE=0): desde la VPS no se puede borrar nada allá, tampoco una clave. Las
# claves anteriores que falta quitar quedan anotadas acá (son IDs, no secretos); preflight.sh y maintenance.sh avisan.
pending_offsite="$SECRETS_DIR/restic-offsite-key-pending"
offsite_append_only() { offsite_configured && [[ "${OFFSITE_PRUNE:-1}" != 1 ]]; }

explain_pending_offsite() {
  local id
  warn "Destino externo de solo agregar (OFFSITE_PRUNE=0): la contraseña ANTERIOR todavía abre el repositorio externo."
  warn "Desde una máquina de confianza con permiso de borrado en el destino (no desde esta VPS), quitá estas claves:"
  while read -r id; do [[ -n "$id" ]] && warn "  restic -r <repositorio externo> key remove $id"; done < "$pending_offsite"
  warn "Después corré «rotate-secrets.sh restic-confirm» de nuevo: comprueba que ya no estén y borra $pending_offsite."
}

# Sin rotación pendiente: comprueba que las claves anotadas ya no estén en el repositorio externo.
check_pending_offsite() {
  local ids id left=()
  offsite_configured || die "Hay claves anotadas en $pending_offsite pero no hay repositorio externo configurado."
  prepare_backup_perms
  ids="$(backup_ops key-ids offsite | tr -d '\r')" || die "No se pudieron listar las claves del repositorio externo."
  while read -r id; do
    [[ -n "$id" ]] || continue
    if grep -qxF "$id" <<<"$ids"; then left+=("$id"); fi
  done < "$pending_offsite"
  if (( ${#left[@]} > 0 )); then
    printf '%s\n' "${left[@]}" > "$pending_offsite"
    explain_pending_offsite
    die "La contraseña anterior de restic todavía abre el repositorio externo."
  fi
  rm -f "$pending_offsite"
  info "Las claves anteriores ya no están en el repositorio externo: rotación de restic terminada."
}

confirm_restic() {
  local prev="$SECRETS_DIR/restic_password.previous" repos r oldmount id
  if [[ ! -s "$prev" ]]; then
    [[ -s "$pending_offsite" ]] || die "No hay una rotación de restic pendiente de confirmar."
    check_pending_offsite
    return 0
  fi
  mapfile -t repos < <(restic_repos)
  confirm_word GUARDADA "Se quitará la contraseña ANTERIOR de restic de: ${repos[*]}. Solo la nueva ($SECRETS_DIR/restic_password) abrirá los backups."
  prepare_backup_perms
  oldmount="$(realpath "$prev"):/run/alvarez/old_password:ro"
  for r in "${repos[@]}"; do
    if [[ "$r" == offsite ]] && offsite_append_only; then
      id="$(backup_ops -v "$oldmount" -- key-id offsite /run/alvarez/old_password | tr -d '\r')"
      if [[ -n "$id" ]]; then
        grep -qxF "$id" "$pending_offsite" 2>/dev/null || echo "$id" >> "$pending_offsite"
        chmod 600 "$pending_offsite"
      fi
      continue
    fi
    backup_ops -v "$oldmount" -- key-remove-for "$r" /run/alvarez/old_password
  done
  rm -f "$prev"
  if [[ -s "$pending_offsite" ]]; then
    info "Contraseña anterior quitada del repositorio local; en el externo queda hasta que la quites desde otra máquina."
    explain_pending_offsite
  else
    info "Rotación de restic terminada: solo la contraseña nueva abre los repositorios."
  fi
}

restart_api=0
# Si un objetivo posterior falla, la API igual se reinicia: con db-app ya rotada, las conexiones nuevas usarían la vieja.
trap 'if (( restart_api )); then warn "Reiniciando la API (rotación incompleta)"; dc restart api >/dev/null || true; fi' EXIT
for what in "$@"; do
  case "$what" in
    db-app) rotate_db_role alvarez_app db_app_password; restart_api=1 ;;
    db-migrator) rotate_db_role alvarez_migrator db_migrator_password ;;
    keyring-cert) rotate_keyring_cert; restart_api=1 ;;
    restic) rotate_restic ;;
    restic-confirm) confirm_restic ;;
  esac
done
if (( restart_api )); then
  restart_api=0
  dc restart api
  wait_healthy api
fi
info "Rotado: $*. Actualizá tu gestor de contraseñas."
