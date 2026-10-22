#!/usr/bin/env bash
# Rotación de secretos sin perder acceso:
#   db-app | db-migrator   nueva contraseña de rol (ALTER ROLE por stdin) y reinicio
#   keyring-cert           nuevo certificado; el anterior queda como previous-*.pfx para descifrar claves viejas
#   restic                 agrega una clave nueva al repositorio y elimina la anterior
source "$(dirname "$0")/lib.sh"
load_env
what="${1:?Uso: rotate-secrets.sh db-app|db-migrator|keyring-cert|restic}"
newpw() { openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-48; }

case "$what" in
  db-app|db-migrator)
    role=$([[ "$what" == db-app ]] && echo alvarez_app || echo alvarez_migrator)
    file="$SECRETS_DIR/$([[ "$what" == db-app ]] && echo db_app_password || echo db_migrator_password)"
    pw="$(newpw)"   # alfanumérica: segura entre comillas simples
    printf "ALTER ROLE %s PASSWORD '%s';\n" "$role" "$pw" | dc exec -T db psql -v ON_ERROR_STOP=1 -U alvarez_admin -d alvarez -q
    printf '%s' "$pw" > "$file.new" && cat "$file.new" > "$file" && rm -f "$file.new"   # mismo inodo: el bind mount lo ve
    [[ "$what" == db-app ]] && dc restart api
    info "Contraseña de $role rotada."
    ;;
  keyring-cert)
    dir="$SECRETS_DIR/keyring"; stamp="$(date -u +%Y%m%d%H%M%S)"
    cp -p "$dir/current.pfx" "$dir/previous-$stamp.pfx"; cp -p "$dir/current.password" "$dir/previous-$stamp.password"
    rm -f "$dir/current.pfx" "$dir/current.password"
    "$ROOT/infra/scripts/bootstrap-secrets.sh" >/dev/null
    dc restart api
    warn "Guardá el certificado NUEVO fuera del servidor. No borres previous-$stamp.* mientras existan claves o backups que lo necesiten (≥ 1 año recomendado)."
    ;;
  restic)
    pw="$(newpw)"; printf '%s' "$pw" > "$SECRETS_DIR/restic_password.new"; chmod 400 "$SECRETS_DIR/restic_password.new"
    dc --profile ops run --rm -v "$SECRETS_DIR/restic_password.new:/run/new_pw:ro" --entrypoint sh backup -c \
      'export RESTIC_PASSWORD_FILE=/run/secrets/restic_password; old=$(restic key list --json | sed -n "s/.*\"current\":true,\"id\":\"\([^\"]*\)\".*/\1/p"); restic key add --new-password-file /run/new_pw && RESTIC_PASSWORD_FILE=/run/new_pw restic key remove "$old"'
    cat "$SECRETS_DIR/restic_password.new" > "$SECRETS_DIR/restic_password"; rm -f "$SECRETS_DIR/restic_password.new"
    warn "Actualizá la contraseña de restic en tu gestor de contraseñas."
    ;;
  *) die "Opción desconocida";;
esac
