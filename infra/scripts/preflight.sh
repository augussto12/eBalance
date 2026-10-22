#!/usr/bin/env bash
# Verificaciones previas al despliegue. Falla cerrado ante configuración o secretos faltantes.
source "$(dirname "$0")/lib.sh"
load_env
fail=0
ok() { echo "OK    $*"; }
bad() { echo "FALLA $*"; fail=1; }

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

avail_kb=$(df -Pk "$ROOT" | awk 'NR==2{print $4}')
(( avail_kb > 2*1024*1024 )) && ok "espacio libre $((avail_kb/1024)) MiB" || bad "menos de 2 GiB libres"

if dc config --format json >/tmp/alvarez-compose.json 2>/dev/null; then
  ok "compose válido"
  if grep -q '"published"' <(python3 -c 'import json,sys;d=json.load(open("/tmp/alvarez-compose.json"));print(json.dumps({k:v.get("ports") for k,v in d["services"].items() if k in("db","api")}))'); then
    bad "db o api publican puertos al host"
  else
    ok "db y api sin puertos publicados"
  fi
  rm -f /tmp/alvarez-compose.json
else
  bad "compose inválido"
fi

if [[ "${SKIP_TLS_CHECK:-0}" != 1 ]]; then
  hostport="${PUBLIC_ORIGIN#https://}"; host="${hostport%%:*}"; [[ "$hostport" == *:* ]] || hostport="$hostport:443"
  if exp=$(echo | timeout 10 openssl s_client -servername "$host" -connect "$hostport" 2>/dev/null | openssl x509 -noout -enddate 2>/dev/null); then
    end="${exp#notAfter=}"
    days=$(( ( $(date -d "$end" +%s) - $(date +%s) ) / 86400 ))
    (( days > 14 )) && ok "TLS válido ($days días restantes)" || bad "TLS vence en $days días"
  else
    warn "No se pudo verificar TLS de $host (¿DNS/certificado aún no configurados?). Usá SKIP_TLS_CHECK=1 solo para el primer arranque."
    fail=1
  fi
fi

if dc ps --status running api >/dev/null 2>&1 && [[ -n "$(dc ps -q api 2>/dev/null)" ]]; then
  dc exec -T api dotnet AlvarezFinanzas.Api.dll check-config && ok "check-config de la API" || bad "check-config de la API"
fi

(( fail == 0 )) && { echo "Preflight OK"; exit 0; } || { echo "Preflight con fallas"; exit 1; }
