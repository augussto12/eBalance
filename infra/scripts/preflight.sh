#!/usr/bin/env bash
# Verificaciones previas al despliegue. Falla cerrado ante configuración o secretos faltantes.
source "$(dirname "$0")/lib.sh"
load_env
fail=0
ok() { echo "OK    $*"; }
bad() { echo "FALLA $*"; fail=1; }
note() { echo "AVISO $*"; }

command -v docker >/dev/null && ok "docker instalado" || bad "docker no instalado"
docker compose version >/dev/null 2>&1 && ok "docker compose disponible" || bad "docker compose no disponible"
[[ "${PUBLIC_ORIGIN:-}" =~ ^https://[a-z0-9.-]+(:[0-9]+)?$ ]] && ok "PUBLIC_ORIGIN https ($PUBLIC_ORIGIN)" || bad "PUBLIC_ORIGIN debe ser https://dominio sin barra final"
[[ "$PUBLIC_ORIGIN" != *TU-DOMINIO* ]] || bad "PUBLIC_ORIGIN todavía tiene el placeholder"
[[ "${WEB_BIND:-127.0.0.1:8088}" == 127.0.0.1:* ]] && ok "web publicado solo en loopback (${WEB_BIND:-127.0.0.1:8088})" || bad "WEB_BIND debe ser 127.0.0.1:PUERTO"

for f in db_admin_password db_app_password db_migrator_password restic_password keyring/current.pfx keyring/current.password; do
  if [[ -s "$SECRETS_DIR/$f" ]]; then
    mode=$(stat -c %a "$SECRETS_DIR/$f")
    [[ "$mode" =~ ^[0-7][0-4]0$ ]] && ok "secreto $f presente (modo $mode)" || bad "secreto $f con permisos amplios ($mode)"
  else
    bad "falta el secreto $f (ejecutá bootstrap-secrets.sh)"
  fi
done
[[ "$(stat -c %a "$SECRETS_DIR")" == 700 ]] && ok "directorio de secretos 700" || bad "el directorio de secretos debe tener modo 700"
if git -C "$ROOT" ls-files --error-unmatch "$SECRETS_DIR" >/dev/null 2>&1; then bad "¡hay secretos versionados en Git!"; else ok "secretos fuera de Git"; fi

check_disks ok bad || true

# Reloj: los códigos TOTP dependen de la hora del servidor.
ntp_rc=0; msg="$(check_ntp)" || ntp_rc=$?
case "$ntp_rc" in 0) ok "$msg" ;; 1) bad "$msg" ;; *) note "$msg" ;; esac

# Copia fuera de la VPS: sin ella, el único backup (y su contraseña) quedan en este servidor.
if offsite_configured; then
  case "$OFFSITE_RESTIC_REPOSITORY" in
    /*|local:*) note "OFFSITE_RESTIC_REPOSITORY es una ruta local: no es una copia fuera de la VPS" ;;
    sftp:*|rclone:*) bad "OFFSITE_RESTIC_REPOSITORY usa ${OFFSITE_RESTIC_REPOSITORY%%:*}, que la imagen de backup no incluye: usá s3:, b2:, azure:, swift: o rest: (RUNBOOK §4)" ;;
    gs:*) bad "OFFSITE_RESTIC_REPOSITORY usa gs:, que necesita un archivo de credenciales de Google que no se monta: para Google Cloud Storage usá su endpoint compatible con S3 (s3:https://storage.googleapis.com/BUCKET/alvarez) con claves HMAC (RUNBOOK §4)" ;;
    *) ok "copia externa configurada (${OFFSITE_RESTIC_REPOSITORY%%:*})" ;;
  esac
  if [[ -e "$OFFSITE_RESTIC_ENV_FILE" ]]; then
    mode=$(stat -c %a "$OFFSITE_RESTIC_ENV_FILE")
    [[ "$mode" =~ ^[0-7][0-4]0$ ]] && ok "credenciales del repositorio externo (modo $mode)" || bad "$OFFSITE_RESTIC_ENV_FILE con permisos amplios ($mode)"
  fi
else
  note "sin copia de backups fuera de la VPS (OFFSITE_RESTIC_REPOSITORY): el único backup está en este servidor (RUNBOOK §4)"
fi
# Sitio del Nginx del host: se instala aparte (install-nginx-site.sh), así que un deploy no lo actualiza.
for site in /etc/nginx/sites-available/zz-alvarez-finanzas.conf /etc/nginx/conf.d/zz-alvarez-finanzas.conf; do
  if [[ -f "$site" ]] && ! grep -q 'alvarez_ip_min' "$site"; then
    note "$site es de una versión anterior (registra la IP completa): reinstalalo con «CERT_DIR=… install-nginx-site.sh NOMBRE --force», que conserva sus listen [::] y default_server (RUNBOOK §3)"
  fi
done
[[ ! -e "$SECRETS_DIR/restic_password.previous" ]] \
  || note "rotación de restic sin confirmar: guardá la contraseña nueva fuera del servidor y corré rotate-secrets.sh restic-confirm"
[[ ! -s "$SECRETS_DIR/restic-offsite-key-pending" ]] \
  || note "la contraseña ANTERIOR de restic todavía abre el repositorio externo de solo agregar: quitá la clave desde otra máquina y corré rotate-secrets.sh restic-confirm (RUNBOOK §7)"

compose_json="$(mktemp)"
if dc config --format json >"$compose_json" 2>/dev/null; then
  ok "compose válido"
  if grep -q '"published"' <(python3 -c 'import json,sys;d=json.load(open(sys.argv[1]));print(json.dumps({k:v.get("ports") for k,v in d["services"].items() if k in("db","api")}))' "$compose_json"); then
    bad "db o api publican puertos al host"
  else
    ok "db y api sin puertos publicados"
  fi
  rm -f "$compose_json"
else
  rm -f "$compose_json"
  bad "compose inválido"
fi

if [[ "${SKIP_TLS_CHECK:-0}" != 1 ]]; then
  if msg="$(check_tls)"; then ok "$msg"; else bad "$msg"; fi
fi

if dc ps --status running api >/dev/null 2>&1 && [[ -n "$(dc ps -q api 2>/dev/null)" ]]; then
  dc exec -T api dotnet AlvarezFinanzas.Api.dll check-config && ok "check-config de la API" || bad "check-config de la API"
fi

(( fail == 0 )) && { echo "Preflight OK"; exit 0; } || { echo "Preflight con fallas"; exit 1; }
