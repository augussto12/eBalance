#!/usr/bin/env bash
# Instalador para la VPS (Ubuntu/Debian), pensado para correrse como root. Conviene correrlo dentro de tmux, para
# que un corte de SSH no lo interrumpa a mitad de camino:
#
#   curl -fsSL https://raw.githubusercontent.com/augussto12/eBalance/implementacion-v1/infra/scripts/vps-install.sh -o vps-install.sh
#   less vps-install.sh                          # revisalo antes de ejecutarlo
#   sudo bash vps-install.sh --inspect           # solo inspecciona (no cambia nada)
#   tmux new -s alvarez 'sudo bash vps-install.sh'
#
# Qué hace (idempotente: se puede volver a correr):
#   1. Inspección SOLO LECTURA: puertos, Nginx, Docker, redes, firewall, memoria, disco. Si algo que no es Nginx
#      ocupa 80/443, o hay conflicto de red, se DETIENE con un reporte (no rompe otros proyectos).
#   2. Instala lo que falte: git, openssl, Docker Engine + Compose v2 + buildx, Nginx (certbot corre en contenedor).
#   3. Descarga el repo en /srv/alvarez-finanzas/app, crea .env y secretos (no los sobrescribe nunca).
#   4. HTTPS sin dominio propio (TLS_MODE=auto): si la VPS tiene un nombre del proveedor que apunta a su IP en el
#      DNS público (p. ej. srvNNNN.hstgr.cloud) se usa ese nombre con certificado normal; si no, certificado de
#      Let's Encrypt para la IP (perfil de ~6 días; certbot 5.8 en contenedor fijado, renovación 2 veces por día).
#   5. Agrega SOLO un sitio a Nginx (no toca nginx.conf ni otros sitios), valida con nginx -t y revierte si falla.
#   6. Construye, migra y levanta la app; backup + prueba de restauración; timers de backup y mantenimiento.
#   7. Te pide crear tu usuario (contraseña oculta, no queda en ningún archivo ni historial).
#
# Variables opcionales: BRANCH (implementacion-v1), COMMIT (fija un commit exacto ya revisado), APP_DIR,
#   PUBLIC_HOST, TLS_MODE (auto|hostname|ip|sslip), ACME_EMAIL, WEB_PORT, PUBLIC_IP, ALLOW_LOW_MEMORY=1.
set -euo pipefail

BRANCH="${BRANCH:-implementacion-v1}"
COMMIT="${COMMIT:-}"
REPO_URL="${REPO_URL:-https://github.com/augussto12/eBalance.git}"
BASE="${BASE:-/srv/alvarez-finanzas}"
APP_DIR="${APP_DIR:-$BASE/app}"
TLS_MODE="${TLS_MODE:-auto}"
LOG="$BASE/install.log"
SUBNET="172.30.57.0/24"
INSPECT=0; [[ "${1:-}" == --inspect ]] && INSPECT=1

say() { printf '\n\033[1;32m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33mAVISO: %s\033[0m\n' "$*" >&2; }
die() { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Corré el instalador como root (sudo bash vps-install.sh)."
command -v apt-get >/dev/null || die "Este instalador soporta Ubuntu/Debian (apt)."
for c in ss ip curl python3; do command -v "$c" >/dev/null || die "Falta el comando «$c» (apt install iproute2 curl python3)."; done
WORK="$(mktemp -d)"
ACME_TEMP=0
cleanup() {
  # Si el instalador se corta con el sitio temporal del desafío ACME puesto, se quita.
  if [[ $ACME_TEMP -eq 1 && -x "$APP_DIR/infra/scripts/install-nginx-site.sh" ]]; then
    "$APP_DIR/infra/scripts/install-nginx-site.sh" --remove-acme >/dev/null 2>&1 || true
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT
if [[ $INSPECT -eq 0 ]]; then
  mkdir -p "$BASE"
  chmod 750 "$BASE"
  # La terminal original queda en 3/4 para pedir los datos del propietario sin que pasen por el registro.
  exec 3>&1 4>&2
  exec > >(tee -a "$LOG") 2>&1
fi
echo "--- $(date -u +%FT%TZ) $([[ $INSPECT -eq 1 ]] && echo inspección || echo instalación) (rama $BRANCH${COMMIT:+, commit $COMMIT})"

# ---------------------------------------------------------------- 1. Inspección (solo lectura)
say "Inspección del servidor (no se modifica nada todavía)"
. /etc/os-release
echo "Sistema: $PRETTY_NAME · $(nproc) CPU · $(free -m | awk '/Mem:/{print $2}') MiB RAM · $(df -Pm / | awk 'NR==2{print $4}') MiB libres en /"

PUBLIC_IP="${PUBLIC_IP:-$(curl -4 -fsS --max-time 8 https://api.ipify.org || hostname -I | awk '{print $1}')}"
[[ "$PUBLIC_IP" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "No pude detectar la IP pública (definí PUBLIC_IP=...)."
echo "IP pública: $PUBLIC_IP"

owner_of_port() { { ss -Htlnp "sport = :$1" 2>/dev/null | grep -o 'users:(("[^"]*"' | head -1 | cut -d'"' -f2; } || true; }
P80="$(owner_of_port 80)"; P443="$(owner_of_port 443)"
echo "Puerto 80: ${P80:-libre} · Puerto 443: ${P443:-libre}"
echo "Puertos escuchando (resumen):"; { ss -Htlnp 2>/dev/null | awk '{print "  " $4 "  " $6}' | sort -u | head -40; } || true

HOST_NGINX=0
systemctl is-active --quiet nginx 2>/dev/null && HOST_NGINX=1
for p in "$P80" "$P443"; do
  if [[ -n "$p" && "$p" != nginx ]]; then
    die "El puerto 80/443 lo usa «$p», no un Nginx del host. Para no romper ese servicio me detengo acá. Copiá todo este reporte y mandámelo para adaptar la instalación."
  fi
done
if [[ -n "$P80$P443" && $HOST_NGINX -eq 0 ]]; then
  die "Hay un nginx en 80/443 que no es el servicio systemd del host (¿en un contenedor?). Me detengo; mandame este reporte."
fi

docker_ports=""
if command -v docker >/dev/null && ! docker info >/dev/null 2>&1; then
  warn "Docker está instalado pero el servicio no responde; se intentará iniciarlo durante la instalación."
fi
if command -v docker >/dev/null && docker info >/dev/null 2>&1; then
  echo "Docker: $(docker --version) · Compose: $(docker compose version 2>/dev/null || echo 'NO')"
  dv="$(docker version -f '{{.Server.Version}}' 2>/dev/null || echo 0)"
  if [[ "$(printf '%s\n28.0.0\n' "$dv" | sort -V | head -1)" != 28.0.0 ]]; then
    warn "Docker $dv: antes de la 28, un puerto publicado en 127.0.0.1 puede alcanzarse desde la red local del proveedor. Conviene actualizar Docker."
  fi
  echo "Contenedores en ejecución:"; { docker ps --format '  {{.Names}} ({{.Image}}) {{.Ports}}' | head -30; } || true
  # Puertos publicados por contenedores: Docker los atiende con DNAT y «ss» puede no mostrarlos.
  docker_ports="$(docker ps --format '{{.Names}} {{.Ports}}' 2>/dev/null | grep -v '^alvarez-finanzas-' || true)"
  if grep -qE '(^|[ ,:])(80|443)->' <<<"$docker_ports"; then
    grep -E '(^|[ ,:])(80|443)->' <<<"$docker_ports" >&2
    die "Un contenedor publica el puerto 80 o 443 directamente (arriba). Me detengo para no romperlo; mandame este reporte."
  fi
  # Solapamiento real de rangos (no comparación de texto) con las redes de Docker de otros proyectos.
  others="$(docker network ls -q | xargs -r docker network inspect -f '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}' 2>/dev/null \
    | grep -v '^alvarez-finanzas_' || true)"
  overlap="$(python3 -c '
import ipaddress, sys
ours = ipaddress.ip_network(sys.argv[1])
for line in sys.stdin:
    name, *nets = line.split() or [""]
    for n in nets:
        try:
            net = ipaddress.ip_network(n, strict=False)
        except ValueError:
            continue
        if net.version == 4 and net.overlaps(ours):
            print(name, n)
' "$SUBNET" <<<"$others")"
  [[ -z "$overlap" ]] || die "Otra red de Docker se superpone con $SUBNET (la necesita esta app): $overlap. Mandame este reporte."
fi
# Rutas del host que se superponen (VPN, redes del proveedor). La de la propia red de la app se ignora.
route_overlap="$(ip -4 route 2>/dev/null | python3 -c '
import ipaddress, sys
ours = ipaddress.ip_network(sys.argv[1])
for line in sys.stdin:
    parts = line.split()
    if not parts or parts[0] == "default":
        continue
    try:
        net = ipaddress.ip_network(parts[0], strict=False)
    except ValueError:
        continue
    if net == ours and "dev" in parts and parts[parts.index("dev") + 1].startswith("br-"):
        continue
    if net.overlaps(ours):
        print(parts[0])
' "$SUBNET")"
[[ -z "$route_overlap" ]] || die "La red $SUBNET se superpone con una ruta del host ($route_overlap). Mandame este reporte."

WEB_PORT="${WEB_PORT:-}"
if [[ -z "$WEB_PORT" && -f "$APP_DIR/.env" ]]; then WEB_PORT="$(sed -n 's/^WEB_BIND=127\.0\.0\.1:\([0-9]*\)$/\1/p' "$APP_DIR/.env" | head -1)"; fi
if [[ -z "$WEB_PORT" ]]; then
  WEB_PORT=8088
  while [[ -n "$(owner_of_port "$WEB_PORT")" ]] || grep -qE "(^|[ ,:])$WEB_PORT->" <<<"$docker_ports"; do WEB_PORT=$((WEB_PORT + 1)); done
fi
echo "Puerto interno (solo 127.0.0.1) para la app: $WEB_PORT"

UFW_ACTIVE=0
if command -v ufw >/dev/null; then
  echo "ufw: $(ufw status | head -1)"
  if ufw status | grep -q "Status: active"; then
    UFW_ACTIVE=1
    ufw status | grep -qE '^(80(/tcp)?|Nginx (HTTP|Full))[[:space:]]+ALLOW' \
      || warn "ufw está activo sin regla para 80/tcp: la instalación abrirá 80 y 443 antes de pedir el certificado."
  fi
fi
echo "Recordá: el firewall del PANEL del proveedor (p. ej. hPanel de Hostinger) también debe permitir 22, 80 y 443."
ip -6 addr show scope global 2>/dev/null | grep -q inet6 && HAS_V6=1 || HAS_V6=0
echo "IPv6 global: $([[ $HAS_V6 -eq 1 ]] && echo sí || echo no)"

# Compilar las imágenes (.NET + Vite) necesita ~2,5 GiB disponibles; con menos, el kernel puede matar procesos de
# OTROS proyectos. Se mide lo disponible ahora (RAM libre + swap libre), no la RAM total.
avail_mb=$(awk '/^MemAvailable:/{a=$2} /^SwapFree:/{s=$2} END{print int((a+s)/1024)}' /proc/meminfo)
echo "Memoria disponible (RAM + swap libres): ${avail_mb} MiB"
if (( avail_mb < 2500 )) && [[ "${ALLOW_LOW_MEMORY:-0}" != 1 ]]; then
  msg="Solo hay ${avail_mb} MiB disponibles: compilar puede hacer que el sistema mate procesos de otros proyectos. Agregá swap (fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile) o corré con ALLOW_LOW_MEMORY=1 bajo tu responsabilidad."
  if [[ $INSPECT -eq 1 ]]; then warn "$msg"; else die "$msg"; fi
fi
(( $(df -Pm / | awk 'NR==2{print $4}') > 6000 )) || die "Hacen falta al menos 6 GiB libres en / para imágenes y backups."

# Resolución por DNS PÚBLICO (la que ve Let's Encrypt), no por /etc/hosts ni el resolver local, que suelen mapear el
# nombre propio del servidor aunque no exista afuera. Se consulta por DoH; si no hay salida, se usa el DNS del sistema.
dns_lookup() { # NOMBRE A|AAAA
  local out
  if out="$(curl -fsS --max-time 8 -H 'accept: application/dns-json' "https://cloudflare-dns.com/dns-query?name=$1&type=$2" 2>/dev/null)"; then
    python3 -c '
import json, sys
want = 1 if sys.argv[1] == "A" else 28
for rr in json.loads(sys.stdin.read()).get("Answer", []):
    if rr.get("type") == want:
        print(rr["data"].lower())
' "$2" <<<"$out" 2>/dev/null || true
  elif [[ "$2" == A ]]; then getent -s dns ahostsv4 "$1" 2>/dev/null | awk '{print $1}' | sort -u || true
  else getent -s dns ahostsv6 "$1" 2>/dev/null | awk '$1 !~ /^::ffff:/{print $1}' | sort -u || true
  fi
}
resolves_to_ip() { dns_lookup "$1" A | grep -qxF "$PUBLIC_IP"; }
normalize_host() { local h="${1,,}"; h="${h%.}"; [[ "$h" =~ ^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$ && "$h" != *..* ]] && echo "$h"; }
provider_hostname() {
  local c n
  for c in "$(hostname -f 2>/dev/null || true)" "$(getent hosts "$PUBLIC_IP" 2>/dev/null | awk '{print $2}' || true)"; do
    n="$(normalize_host "$c")" || continue
    [[ "$n" == *.* && "$n" != *localhost* && "$n" != *.sslip.io && "$n" != *.nip.io && ! "$n" =~ ^[0-9.]+$ ]] || continue
    resolves_to_ip "$n" && { echo "$n"; return 0; }
  done
  return 1
}

# En una re-ejecución sin indicaciones explícitas se respeta la dirección que ya está en .env.
ENV_HOST=""
if [[ -f "$APP_DIR/.env" ]]; then ENV_HOST="$(sed -n 's#^PUBLIC_ORIGIN=https://##p' "$APP_DIR/.env" | head -1)"; fi
if [[ -n "${PUBLIC_HOST:-}" ]]; then
  PUBLIC_HOST="$(normalize_host "$PUBLIC_HOST")" || die "PUBLIC_HOST no es un nombre válido."
fi
if [[ "$TLS_MODE" == auto ]]; then
  if [[ -n "${PUBLIC_HOST:-}" ]]; then TLS_MODE=hostname
  elif [[ -n "$ENV_HOST" ]]; then
    PUBLIC_HOST="$ENV_HOST"
    if [[ "$ENV_HOST" =~ ^[0-9.]+$ ]]; then TLS_MODE=ip
    elif [[ "$ENV_HOST" == *.sslip.io ]]; then TLS_MODE=sslip
    else TLS_MODE=hostname; fi
    echo "Se mantiene la dirección configurada en .env: $ENV_HOST"
  elif PUBLIC_HOST="$(provider_hostname)"; then TLS_MODE=hostname; echo "Nombre del proveedor que apunta a esta IP: $PUBLIC_HOST"
  else TLS_MODE=ip; fi
fi
case "$TLS_MODE" in
  hostname) [[ -n "${PUBLIC_HOST:-}" ]] || die "TLS_MODE=hostname requiere PUBLIC_HOST."
            [[ ! "$PUBLIC_HOST" =~ ^[0-9.]+$ ]] || die "PUBLIC_HOST es una IP: usá TLS_MODE=ip." ;;
  ip) PUBLIC_HOST="$PUBLIC_IP" ;;
  sslip) PUBLIC_HOST="${PUBLIC_HOST:-${PUBLIC_IP//./-}.sslip.io}"
         warn "sslip.io es un DNS de terceros compartido (mismo «sitio» que miles de páginas ajenas). Preferí un dominio propio o TLS_MODE=ip." ;;
  *) die "TLS_MODE debe ser auto, hostname, ip o sslip" ;;
esac
if [[ "$TLS_MODE" != ip ]]; then
  resolves_to_ip "$PUBLIC_HOST" || die "$PUBLIC_HOST no resuelve a $PUBLIC_IP en el DNS público."
  # Let's Encrypt prefiere IPv6: un AAAA que no es de esta VPS hace fallar la validación.
  for v6 in $(dns_lookup "$PUBLIC_HOST" AAAA); do
    ip -6 addr show 2>/dev/null | grep -qi "inet6 $v6/" \
      || die "$PUBLIC_HOST tiene un registro AAAA ($v6) que no es de esta VPS: Let's Encrypt validaría contra otro servidor."
  done
fi
if [[ -n "$ENV_HOST" && "$ENV_HOST" != "$PUBLIC_HOST" ]]; then
  warn "La dirección cambia de https://$ENV_HOST a https://$PUBLIC_HOST (se actualizan .env, certificado y sitio)."
fi

NGX_AVAIL=/etc/nginx/conf.d; [[ -d /etc/nginx/sites-available ]] && NGX_AVAIL=/etc/nginx/sites-available
OWN_SITE="$NGX_AVAIL/zz-alvarez-finanzas.conf"
# Convivencia con otros sitios del Nginx existente. Se excluyen los archivos propios y los comentarios.
if [[ $HOST_NGINX -eq 1 ]]; then
  nginx -T >"$WORK/nginx-T.txt" 2>"$WORK/nginx-T.err" \
    || die "nginx -t ya falla ANTES de instalar (otro sitio está roto): $(tail -3 "$WORK/nginx-T.err"). No toco nada."
  others_conf="$(awk '/^# configuration file /{skip=($0 ~ /zz-alvarez-finanzas/)} !skip' "$WORK/nginx-T.txt" | sed 's/#.*//')"
  if [[ "$TLS_MODE" == ip ]]; then
    # Con certificado de IP (sin SNI) este sitio tiene que ser el default del 443 y adueñarse de http(s)://IP.
    if grep -qE '^[[:space:]]*listen[^;]*\b443\b[^;]*default_server' <<<"$others_conf"; then
      die "Otro sitio ya es el default del puerto 443. Con certificado por IP chocarían. Usá un nombre (PUBLIC_HOST=...) o mandame este reporte."
    fi
    if ! grep -qs "server_name $PUBLIC_IP;" "$OWN_SITE"; then
      for scheme in http https; do
        code="$(curl -sk -o "$WORK/probe.html" -w '%{http_code}' --max-time 5 "$scheme://$PUBLIC_IP/" 2>/dev/null || true)"
        if [[ -n "$code" && "$code" != 000 ]] && ! grep -qiE 'Welcome to nginx' "$WORK/probe.html"; then
          die "$scheme://$PUBLIC_IP ya sirve otro contenido (HTTP $code). No lo piso. Usá un nombre (PUBLIC_HOST=...) o mandame este reporte."
        fi
      done
    fi
  fi
  if grep -E '^[[:space:]]*server_name' <<<"$others_conf" | grep -qwF -- "$PUBLIC_HOST"; then
    die "Otro sitio de Nginx ya usa el nombre $PUBLIC_HOST. Me detengo para no pisarlo."
  fi
  if grep -qE '^[[:space:]]*listen[^;]*\bssl\b' <<<"$others_conf"; then
    warn "Hay otros sitios HTTPS en este Nginx. Las cookies de la app son «__Host-» (ligadas al nombre exacto); evitá publicar otros servicios bajo https://$PUBLIC_HOST en otros puertos."
  fi
fi
# Un certbot de otros proyectos en modo standalone necesita el 80 libre: si se instala Nginx, dejaría de renovar.
if [[ $HOST_NGINX -eq 0 ]] && grep -rqs '^authenticator = standalone' /etc/letsencrypt/renewal/; then
  die "Hay certificados de otros proyectos que se renuevan con certbot --standalone (puerto 80). Instalar Nginx los rompería. Mandame este reporte."
fi
echo "Dirección pública de la app: https://$PUBLIC_HOST (modo TLS: $TLS_MODE)"
if [[ $INSPECT -eq 1 ]]; then
  say "Inspección terminada: no se cambió nada. Si no hubo errores, podés correr la instalación completa."
  exit 0
fi

# ---------------------------------------------------------------- 2. Paquetes
say "Instalando lo que falte"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq ca-certificates curl git openssl python3 >/dev/null
if ! command -v docker >/dev/null; then
  # Repositorio oficial de Docker (Engine + Compose v2 + buildx con versiones al día).
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL "https://download.docker.com/linux/$ID/gpg" -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/$ID ${VERSION_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -qq
  apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin >/dev/null
  systemctl enable --now docker
fi
systemctl is-active --quiet docker || systemctl enable --now docker
docker compose version >/dev/null 2>&1 || apt-get install -y -qq docker-compose-plugin >/dev/null 2>&1 || apt-get install -y -qq docker-compose-v2 >/dev/null
docker buildx version >/dev/null 2>&1 || apt-get install -y -qq docker-buildx-plugin >/dev/null 2>&1 || apt-get install -y -qq docker-buildx >/dev/null
if [[ $HOST_NGINX -eq 0 ]]; then
  apt-get install -y -qq nginx >/dev/null
  systemctl enable --now nginx
  HOST_NGINX=1
fi
echo "Docker $(docker --version | awk '{print $3}' | tr -d ,) · Compose $(docker compose version --short) · nginx $(nginx -v 2>&1 | cut -d/ -f2)"
# certbot NO se instala en el sistema: corre en contenedor fijado por digest (infra/scripts/certbot.sh), con
# configuración aislada en /etc/alvarez-finanzas/letsencrypt. El certbot que tengas para otros sitios no se toca.

# ---------------------------------------------------------------- 3. Código, .env y secretos
say "Código en $APP_DIR (rama $BRANCH${COMMIT:+, commit $COMMIT})"
[[ -d "$APP_DIR/.git" ]] || git clone -q --branch "$BRANCH" "$REPO_URL" "$APP_DIR"
git -C "$APP_DIR" fetch -q origin "$BRANCH"
if [[ -n "$COMMIT" ]]; then
  # Un commit exacto (ya revisado) evita ejecutar como root lo que llegue después a la rama.
  git -C "$APP_DIR" cat-file -e "$COMMIT^{commit}" 2>/dev/null || die "El commit $COMMIT no existe en el repositorio."
  git -C "$APP_DIR" merge-base --is-ancestor "$COMMIT" "origin/$BRANCH" || die "El commit $COMMIT no pertenece a origin/$BRANCH."
  git -C "$APP_DIR" checkout -q --detach "$COMMIT"
else
  git -C "$APP_DIR" checkout -q "$BRANCH" 2>/dev/null || git -C "$APP_DIR" checkout -q -b "$BRANCH" "origin/$BRANCH"
  git -C "$APP_DIR" merge -q --ff-only "origin/$BRANCH" || die "Hay cambios locales en $APP_DIR que impiden actualizar."
fi
echo "Commit: $(git -C "$APP_DIR" rev-parse HEAD)"
cd "$APP_DIR"
if [[ ! -f .env ]]; then
  cat > .env <<EOF
COMPOSE_PROJECT_NAME=alvarez-finanzas
PUBLIC_ORIGIN=https://$PUBLIC_HOST
IMAGE_TAG=local
WEB_BIND=127.0.0.1:$WEB_PORT
SECRETS_DIR=$BASE/secrets
BACKUP_DIR=$BASE/backups
RESTIC_REPOSITORY=/backups/restic
EOF
  chmod 600 .env
fi
./infra/scripts/bootstrap-secrets.sh

# ---------------------------------------------------------------- 4. Firewall (antes del desafío ACME, que entra por el 80)
if [[ $UFW_ACTIVE -eq 1 ]]; then
  ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null
  echo "ufw: se permitieron 80 y 443 (no se cambió nada más)."
fi

# ---------------------------------------------------------------- 5. Certificado y sitio Nginx
say "HTTPS para $PUBLIC_HOST"
CERT_DIR=/etc/alvarez-finanzas/letsencrypt/live/alvarez-finanzas
# «listen [::]» solo si hay IPv6 y Nginx ya escucha en IPv6 (si no, este sitio pasaría a ser el default de IPv6).
NO_IPV6=1
if [[ $HAS_V6 -eq 1 && "$TLS_MODE" != ip ]] && ss -Htln 2>/dev/null | awk '{print $4}' | grep -qE '^\[::\]:(80|443)$'; then NO_IPV6=""; fi
export NO_IPV6
# ¿Hay un certificado para ESTE nombre/IP con al menos $1 horas por delante?
covers_host() {
  [[ -s "$CERT_DIR/fullchain.pem" ]] || return 1
  openssl x509 -in "$CERT_DIR/fullchain.pem" -noout -checkend $(( $1 * 3600 )) >/dev/null 2>&1 || return 1
  openssl x509 -in "$CERT_DIR/fullchain.pem" -noout -ext subjectAltName 2>/dev/null \
    | grep -qE "(DNS:|IP Address:)$(sed 's/\./\\./g' <<<"$PUBLIC_HOST")(,|$)"
}
# 72 h: certbot renueva los de ~6 días a mitad de vida (~80 h) y los de 90 días con 30 días por delante.
if ! covers_host 72; then
  # Sitio temporal SOLO en :80 y SOLO para este nombre/IP, salvo que el sitio final ya atienda este nombre.
  if ! grep -qs "server_name $PUBLIC_HOST;" "$OWN_SITE"; then
    ACME_TEMP=1
    ./infra/scripts/install-nginx-site.sh --acme-only "$PUBLIC_HOST"
  fi
  kind=host; [[ "$TLS_MODE" == ip ]] && kind=ip
  if ! ./infra/scripts/certbot.sh issue "$kind" "$PUBLIC_HOST"; then
    die "No se pudo obtener el certificado para $PUBLIC_HOST. Revisá que el puerto 80 esté abierto en el firewall del PANEL de la VPS (y «ufw allow 80/tcp» si usás ufw) y mandame este reporte."
  fi
  covers_host 1 || die "El certificado obtenido no cubre $PUBLIC_HOST."
fi
DEFAULT_443=""; [[ "$TLS_MODE" == ip ]] && DEFAULT_443=1
DEFAULT_443="$DEFAULT_443" CERT_DIR="$CERT_DIR" ./infra/scripts/install-nginx-site.sh "$PUBLIC_HOST" --force
ACME_TEMP=0
# El timer de renovación va apenas existe el certificado: si algo posterior falla, igual se renueva.
sed "s#/srv/alvarez-finanzas/app#$APP_DIR#g" infra/systemd/alvarez-certbot.service > /etc/systemd/system/alvarez-certbot.service
cp infra/systemd/alvarez-certbot.timer /etc/systemd/system/alvarez-certbot.timer
systemctl daemon-reload
systemctl enable --now alvarez-certbot.timer >/dev/null
# .env cambia recién con el certificado válido (si cambió la dirección, también el origen permitido de la API).
if ! grep -qx "PUBLIC_ORIGIN=https://$PUBLIC_HOST" .env; then
  sed -i "s#^PUBLIC_ORIGIN=.*#PUBLIC_ORIGIN=https://$PUBLIC_HOST#" .env
  grep -qx "PUBLIC_ORIGIN=https://$PUBLIC_HOST" .env || echo "PUBLIC_ORIGIN=https://$PUBLIC_HOST" >> .env
  echo ".env: PUBLIC_ORIGIN=https://$PUBLIC_HOST"
fi

# ---------------------------------------------------------------- 6. Despliegue
say "Construyendo y levantando la app (la primera vez tarda varios minutos)"
if docker volume inspect alvarez-finanzas_db-data >/dev/null 2>&1; then
  ./infra/scripts/deploy.sh
else
  ./infra/scripts/deploy.sh --first
fi
./infra/scripts/preflight.sh

say "Backup inicial y prueba de restauración aislada"
./infra/scripts/backup.sh </dev/null
./infra/scripts/restore-test.sh </dev/null

say "Timers: renovación TLS (2 por día), backup y mantenimiento (diarios)"
for unit in infra/systemd/alvarez-*; do
  sed "s#/srv/alvarez-finanzas/app#$APP_DIR#g" "$unit" > "/etc/systemd/system/$(basename "$unit")"
done
systemctl daemon-reload
systemctl enable --now alvarez-certbot.timer alvarez-backup.timer alvarez-maintenance.timer >/dev/null
systemctl list-timers 'alvarez-*' --no-pager | head -4 || true

# ---------------------------------------------------------------- 7. Propietario
say "Tu usuario"
owners="$(docker compose --env-file .env -f compose.yaml exec -T db psql -U alvarez_admin -d alvarez -tAc 'select count(*) from "AspNetUsers"' 2>/dev/null | tr -d '[:space:]' || true)"
case "$owners" in
  0)
    if { : </dev/tty; } 2>/dev/null; then
      # Directo con la terminal (no pasa por el registro): la contraseña se escribe oculta y se pide dos veces.
      read -r -p "Nombre de usuario para ingresar (ej. augusto): " owner_user </dev/tty 2>&4
      read -r -p "Nombre para mostrar (ej. Augusto Álvarez): " owner_name </dev/tty 2>&4
      ./infra/scripts/bootstrap-owner.sh --username "$owner_user" --display-name "$owner_name" </dev/tty >&3 2>&4
      echo "Propietario creado: $owner_user"
    else
      warn "Sin terminal interactiva: creá tu usuario con  cd $APP_DIR && ./infra/scripts/bootstrap-owner.sh --username TU_USUARIO --display-name 'Tu Nombre'"
    fi ;;
  ''|*[!0-9]*)
    warn "No pude consultar la base para saber si existe el propietario. Si no lo creaste: cd $APP_DIR && ./infra/scripts/bootstrap-owner.sh --username TU_USUARIO --display-name 'Tu Nombre'" ;;
  *) echo "El propietario ya existe." ;;
esac

cat <<MSG

========================================================================
 Listo:  https://$PUBLIC_HOST
------------------------------------------------------------------------
 1. Entrá, ingresá con tu usuario y configurá el autenticador (QR).
    Guardá los 10 códigos de recuperación en tu gestor de contraseñas.
 2. Guardá FUERA del servidor:  $BASE/secrets/restic_password
    y  $BASE/secrets/keyring/  (current.pfx + current.password).
 3. Cambiá ya la contraseña de root (quedó compartida):  passwd
    Recomendado: entrar por SSH con claves y desactivar el login por contraseña.
 4. Configurá una copia de backups fuera de la VPS (docs/RUNBOOK.md §4).
 Registro de esta instalación: $LOG (sin secretos ni montos).
========================================================================
MSG
