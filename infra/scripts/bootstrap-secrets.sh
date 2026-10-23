#!/usr/bin/env bash
# Genera secretos aleatorios y el certificado que cifra el key ring de Data Protection.
# Idempotente: NO sobrescribe secretos existentes (perderlos impide descifrar datos o acceder a la base).
# Requiere ejecutarse con permisos para chown (sudo) en la VPS.
source "$(dirname "$0")/lib.sh"
load_env
command -v openssl >/dev/null || die "Falta openssl."

umask 077
mkdir -p "$SECRETS_DIR/keyring"
chmod 700 "$SECRETS_DIR"

gen() {
  local file="$SECRETS_DIR/$1"
  if [[ -s "$file" ]]; then info "$1 ya existe (sin cambios)"; return; fi
  openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-48 > "$file"
  info "$1 generado"
}

gen db_admin_password
gen db_app_password
gen db_migrator_password
gen restic_password

if [[ ! -s "$SECRETS_DIR/keyring/current.pfx" ]]; then
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-48 > "$SECRETS_DIR/keyring/current.password"
  openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 3650 -subj "/CN=alvarez-finanzas-keyring" \
    -keyout "$tmp/key.pem" -out "$tmp/cert.pem" 2>/dev/null
  openssl pkcs12 -export -inkey "$tmp/key.pem" -in "$tmp/cert.pem" -out "$SECRETS_DIR/keyring/current.pfx" \
    -passout "file:$SECRETS_DIR/keyring/current.password" -keypbe AES-256-CBC -certpbe AES-256-CBC -macalg sha256
  info "Certificado del key ring generado (10 años)."
else
  info "keyring/current.pfx ya existe (sin cambios)"
fi

# Permisos mínimos: cada contenedor lee solo lo que necesita.
fix() { local f="$1" owner="$2" mode="$3"; [[ -e "$f" ]] || return 0; chown "$owner" "$f" 2>/dev/null || warn "No se pudo chown $f (¿falta sudo?)"; chmod "$mode" "$f"; }
fix "$SECRETS_DIR/db_admin_password"    "$PG_UID:$PG_UID"   0400
fix "$SECRETS_DIR/db_app_password"      "$PG_UID:$APP_UID"  0440
fix "$SECRETS_DIR/db_migrator_password" "$PG_UID:$APP_UID"  0440
# restic: lo lee el contenedor de backup, que corre como $BACKUP_UID (sin root).
fix "$SECRETS_DIR/restic_password"      "0:$BACKUP_UID"     0440
fix "$OFFSITE_RESTIC_ENV_FILE"          "0:$BACKUP_UID"     0440
chown -R "$APP_UID:$APP_UID" "$SECRETS_DIR/keyring" 2>/dev/null || warn "No se pudo chown keyring (¿falta sudo?)"
chmod 500 "$SECRETS_DIR/keyring"
chmod 400 "$SECRETS_DIR"/keyring/*

cat <<MSG

Secretos listos en $SECRETS_DIR (fuera de Git).
IMPORTANTE — guardá FUERA del servidor, en tu gestor de contraseñas:
  • $SECRETS_DIR/restic_password       (sin ella los backups son ilegibles)
  • $SECRETS_DIR/keyring/current.pfx y current.password (sin ellos no se descifran los segundos factores restaurados)
No los copies junto al único backup.
MSG
