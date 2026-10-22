#!/usr/bin/env bash
# Instalador para la VPS (Ubuntu/Debian), pensado para correrse como root. Conviene correrlo dentro de tmux, para
# que un corte de SSH no lo interrumpa a mitad de camino:
#
#   curl -fsSL https://raw.githubusercontent.com/augussto12/eBalance/implementacion-v1/infra/scripts/vps-install.sh -o vps-install.sh
#   less vps-install.sh                          # revisalo antes de ejecutarlo
#   sudo bash vps-install.sh --inspect           # solo inspecciona (no cambia nada)
#   tmux new -s alvarez                          # sesión que sobrevive a un corte de SSH (tmux attach -t alvarez)
#   sudo bash vps-install.sh                     # dentro de tmux
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
#   PUBLIC_HOST, TLS_MODE (auto|hostname|ip|sslip), ACME_EMAIL, WEB_PORT, PUBLIC_IP, ALLOW_LOW_MEMORY=1,
#   ALLOW_TAKEOVER=1 (solo después de revisar qué publica hoy el servidor por defecto de Nginx).
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
BLOCKERS=0
# Conflicto con lo que ya hay en el servidor: --inspect los informa TODOS y sigue; la instalación se detiene en el primero.
block() { if [[ $INSPECT -eq 1 ]]; then printf '\033[1;31mBLOQUEA LA INSTALACIÓN: %s\033[0m\n' "$*" >&2; BLOCKERS=$((BLOCKERS + 1)); else die "$*"; fi; }

[[ $EUID -eq 0 ]] || die "Corré el instalador como root (sudo bash vps-install.sh)."
command -v apt-get >/dev/null || die "Este instalador soporta Ubuntu/Debian (apt)."
for c in ss ip curl python3; do command -v "$c" >/dev/null || die "Falta el comando «$c» (apt install iproute2 curl python3)."; done
WORK="$(mktemp -d)"
ACME_TEMP=0
POLICY_CREATED=0
cleanup() {
  # Si el instalador se corta con el sitio temporal del desafío ACME puesto, se quita.
  if [[ $ACME_TEMP -eq 1 && -x "$APP_DIR/infra/scripts/install-nginx-site.sh" ]]; then
    "$APP_DIR/infra/scripts/install-nginx-site.sh" --remove-acme >/dev/null 2>&1 || true
  fi
  if [[ $POLICY_CREATED -eq 1 ]]; then rm -f /usr/sbin/policy-rc.d; fi
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
    block "El puerto 80/443 lo usa «$p», no un Nginx del host. Para no romper ese servicio me detengo acá. Copiá todo este reporte y mandámelo para adaptar la instalación."
  fi
done
if [[ -n "$P80$P443" && $HOST_NGINX -eq 0 ]]; then
  block "Hay un nginx en 80/443 que no es el servicio systemd del host (¿en un contenedor?). Me detengo; mandame este reporte."
fi

docker_ports=""
if command -v docker >/dev/null; then
  if [[ "$(readlink -f "$(command -v docker)")" == /snap/* ]] || { command -v snap >/dev/null && snap list docker >/dev/null 2>&1; }; then
    block "Docker está instalado como snap: no puede usar /srv ni /etc del host y su servicio no es docker.service. Hace falta Docker Engine nativo (docker-ce); no lo cambio solo porque otros proyectos dependen de él. Mandame este reporte."
  fi
  if docker --version 2>/dev/null | grep -qi podman; then
    block "«docker» es Podman (podman-docker), no Docker Engine. Mandame este reporte."
  fi
  if ! docker compose version >/dev/null 2>&1 \
     && ! apt-cache policy docker-compose-plugin docker-compose-v2 2>/dev/null | grep -q 'Candidate: [0-9]'; then
    block "Este Docker no tiene Compose v2 y la distribución no lo ofrece (¿docker.io de Debian?). Pasar a docker-ce afecta a los otros proyectos: mandame este reporte."
  fi
  if ! docker buildx version >/dev/null 2>&1 \
     && ! apt-cache policy docker-buildx-plugin docker-buildx 2>/dev/null | grep -q 'Candidate: [0-9]'; then
    block "Este Docker no tiene buildx y la distribución no lo ofrece. Mandame este reporte."
  fi
  if [[ -f /etc/docker/daemon.json ]] \
     && python3 -c 'import json,sys; sys.exit(0 if json.load(open("/etc/docker/daemon.json")).get("iptables") is False else 1)' 2>/dev/null; then
    warn "/etc/docker/daemon.json tiene \"iptables\": false: los contenedores pueden no tener salida a internet (la compilación y certbot la necesitan)."
  fi
else
  # Sin el CLI de Docker se instala docker-ce, cuyo containerd.io reemplaza al containerd/runc de la distribución.
  distro_rt="$(dpkg-query -W -f='${Package} ${db:Status-Abbrev}\n' containerd runc 2>/dev/null | awk '$2 ~ /^ii/{print $1}' | tr '\n' ' ' || true)"
  if [[ -n "$distro_rt" ]]; then
    if systemctl is-active --quiet containerd 2>/dev/null; then
      block "containerd de la distribución está activo ($distro_rt) sin Docker: lo usa otro runtime (¿Kubernetes, nerdctl?). Instalar docker-ce lo reemplazaría. Mandame este reporte."
    else
      warn "Paquetes de la distribución que docker-ce va a reemplazar (no están en uso): $distro_rt"
    fi
  fi
fi
if command -v docker >/dev/null && ! docker info >/dev/null 2>&1; then
  warn "Docker está instalado pero el servicio no responde; se intentará iniciarlo durante la instalación."
fi
if command -v docker >/dev/null && docker info >/dev/null 2>&1; then
  echo "Docker: $(docker --version) · Compose: $(docker compose version 2>/dev/null || echo 'NO')"
  dv="$(docker version -f '{{.Server.Version}}' 2>/dev/null || echo 0)"
  if [[ "$(printf '%s\n23.0.0\n' "$dv" | sort -V | head -1)" != 23.0.0 ]]; then
    block "Docker $dv es muy viejo (hacen falta 23 o más, con BuildKit). Actualizarlo afecta a los otros proyectos: mandame este reporte."
  fi
  if [[ "$(printf '%s\n28.0.0\n' "$dv" | sort -V | head -1)" != 28.0.0 ]]; then
    warn "Docker $dv: antes de la 28, un puerto publicado en 127.0.0.1 puede alcanzarse desde la red local del proveedor. Conviene actualizar Docker."
  fi
  echo "Contenedores en ejecución:"; { docker ps --format '  {{.Names}} ({{.Image}}) {{.Ports}}' | head -30; } || true
  # Puertos publicados por contenedores: Docker los atiende con DNAT y «ss» puede no mostrarlos.
  docker_ports="$(docker ps --format '{{.Names}} {{.Ports}}' 2>/dev/null | grep -v '^alvarez-finanzas-' || true)"
  if grep -qE '(^|[ ,:])(80|443)->' <<<"$docker_ports"; then
    grep -E '(^|[ ,:])(80|443)->' <<<"$docker_ports" >&2
    block "Un contenedor publica el puerto 80 o 443 directamente (arriba). Me detengo para no romperlo; mandame este reporte."
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
  [[ -z "$overlap" ]] || block "Otra red de Docker se superpone con $SUBNET (la necesita esta app): $overlap. Mandame este reporte."
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
[[ -z "$route_overlap" ]] || block "La red $SUBNET se superpone con una ruta del host ($route_overlap). Mandame este reporte."

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
  block "$msg"
fi
# Primera instalación: imágenes (~2,5 GB) + caché de build (~1 GB) + el margen de 2 GiB que exige preflight.
# En una re-ejecución las imágenes ya existen.
free_mb=$(df -Pm / | awk 'NR==2{print $4}'); need_mb=8200
if [[ -f "$APP_DIR/.env" ]] && command -v docker >/dev/null \
   && docker image inspect "alvarez-finanzas-api:$(sed -n 's/^IMAGE_TAG=//p' "$APP_DIR/.env" | head -1)" >/dev/null 2>&1; then need_mb=3000; fi
(( free_mb > need_mb )) || block "Hay ${free_mb} MiB libres en /; hacen falta al menos ${need_mb} MiB para imágenes, compilación y backups."

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
  resolves_to_ip "$PUBLIC_HOST" || block "$PUBLIC_HOST no resuelve a $PUBLIC_IP en el DNS público."
  # Let's Encrypt prefiere IPv6: un AAAA que no es de esta VPS hace fallar la validación.
  for v6 in $(dns_lookup "$PUBLIC_HOST" AAAA); do
    ip -6 addr show 2>/dev/null | grep -qi "inet6 $v6/" \
      || block "$PUBLIC_HOST tiene un registro AAAA ($v6) que no es de esta VPS: Let's Encrypt validaría contra otro servidor."
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
  if [[ "$TLS_MODE" == ip ]] && grep -qE '^[[:space:]]*listen[^;]*\b443\b[^;]*default_server' <<<"$others_conf"; then
    # Con certificado de IP (sin SNI) este sitio tiene que ser el default del 443.
    block "Otro sitio ya es el default del puerto 443. Con certificado por IP chocarían. Usá un nombre (PUBLIC_HOST=...) o mandame este reporte."
  fi
  if grep -E '^[[:space:]]*server_name' <<<"$others_conf" | grep -qwF -- "$PUBLIC_HOST"; then
    block "Otro sitio de Nginx ya usa el nombre $PUBLIC_HOST. Me detengo para no pisarlo."
  fi
  # Hoy, los pedidos a $PUBLIC_HOST los atiende el servidor por defecto de cada puerto. Si ese servidor publica algo
  # más que la página de bienvenida (rutas con proxy_pass, alias, etc.), este sitio se lo «quitaría» para este nombre.
  if ! grep -qs "server_name $PUBLIC_HOST;" "$OWN_SITE" && [[ "${ALLOW_TAKEOVER:-0}" != 1 ]]; then
    takeover="$(python3 - "$WORK/nginx-T.txt" <<'PY'
import re, sys
text = open(sys.argv[1], encoding="utf-8", errors="replace").read()
servers, cur, depth, stack, buf = [], None, 0, [], ""
fname = "?"
for raw in text.splitlines():
    m = re.match(r"# configuration file (.+):$", raw)
    if m:
        fname = m.group(1); continue
    line = re.sub(r"#.*", "", raw)
    for ch in line + "\n":
        if ch in "{};":
            stmt = buf.strip(); buf = ""
            if ch == "{":
                stack.append(stmt)
                if stmt == "server" and cur is None and "zz-alvarez-finanzas" not in fname:
                    cur = {"file": fname, "depth": len(stack), "listen": [], "items": []}
                elif cur is not None and stmt.startswith("location"):
                    cur["items"].append(stmt)
            elif ch == "}":
                if cur is not None and len(stack) == cur["depth"]:
                    servers.append(cur); cur = None
                if stack: stack.pop()
            elif cur is not None and stmt:
                word = stmt.split()[0]
                if word == "listen":
                    cur["listen"].append(stmt)
                elif word in ("proxy_pass", "fastcgi_pass", "uwsgi_pass", "scgi_pass", "grpc_pass", "alias"):
                    cur["items"].append(stmt)
        else:
            buf += ch
def port_of(listen):
    tok = listen.split()[1]
    m = re.search(r"(\d+)$", tok)
    return int(m.group(1)) if m else 80
for port in (80, 443):
    cands = [s for s in servers if any(port_of(l) == port for l in s["listen"])]
    dflt = [s for s in cands if any(port_of(l) == port and re.search(r"\bdefault(_server)?\b", l) for l in s["listen"])] or cands[:1]
    for s in dflt:
        risky = [i for i in s["items"] if not re.fullmatch(r"location\s+(/|~\s+/\\\.ht)", i)]
        if risky:
            print(f"{port} {s['file']}: " + "; ".join(risky[:6]))
PY
)"
    if [[ -n "$takeover" ]]; then
      echo "$takeover" >&2
      block "El servidor por defecto de Nginx publica rutas propias (arriba). Este sitio las tomaría para $PUBLIC_HOST. Mandame este reporte; si lo revisaste y no se usan con esta dirección, ALLOW_TAKEOVER=1."
    fi
    for scheme in http https; do
      port=80; [[ $scheme == https ]] && port=443
      code="$(curl -sk -o "$WORK/probe.html" -w '%{http_code}' --max-time 5 --connect-to "$PUBLIC_HOST:$port:$PUBLIC_IP:$port" "$scheme://$PUBLIC_HOST/" 2>/dev/null || true)"
      if [[ -z "$code" || "$code" == 000 ]]; then
        code="$(curl -sk -o "$WORK/probe.html" -w '%{http_code}' --max-time 5 --connect-to "$PUBLIC_HOST:$port:127.0.0.1:$port" "$scheme://$PUBLIC_HOST/" 2>/dev/null || true)"
      fi
      if [[ -n "$code" && "$code" != 000 ]] && ! grep -qiE 'Welcome to nginx' "$WORK/probe.html"; then
        block "$scheme://$PUBLIC_HOST ya responde con otro contenido (HTTP $code). No lo piso. Mandame este reporte; si lo revisaste, ALLOW_TAKEOVER=1."
      fi
    done
  fi
fi
# Las cookies «__Host-» no se aíslan por puerto: otro servicio HTTPS en esta misma dirección las recibiría.
tls_ports=""
for port in $( { ss -Htln 2>/dev/null | awk '{print $4}' | grep -vE '^(127\.|\[::1\])' | sed -E 's/.*:([0-9]+)$/\1/'; \
                 grep -oE '(0\.0\.0\.0|\[::\]|:::?):[0-9]+->' <<<"$docker_ports" | grep -oE '[0-9]+->' | tr -d '>-'; } | sort -nu | head -60); do
  [[ "$port" =~ ^(22|80|443)$ ]] && continue
  sni=(-servername "$PUBLIC_HOST"); [[ "$TLS_MODE" == ip ]] && sni=()
  if timeout 3 openssl s_client -connect "127.0.0.1:$port" "${sni[@]}" </dev/null 2>/dev/null | grep -q 'BEGIN CERTIFICATE'; then tls_ports+=" $port"; fi
done
if [[ -n "$tls_ports" ]]; then
  warn "Otros servicios HTTPS en esta VPS (puertos:$tls_ports). Si los abrís como https://$PUBLIC_HOST:<puerto>, el navegador también les envía la cookie de sesión de la app: abrilos con otra dirección o con otro perfil del navegador."
fi
# Un certbot de otros proyectos en modo standalone necesita el 80 libre: si se instala Nginx, dejaría de renovar.
if [[ $HOST_NGINX -eq 0 ]] && grep -rqs '^authenticator = standalone' /etc/letsencrypt/renewal/; then
  block "Hay certificados de otros proyectos que se renuevan con certbot --standalone (puerto 80). Instalar Nginx los rompería. Mandame este reporte."
fi
echo "Dirección pública de la app: https://$PUBLIC_HOST (modo TLS: $TLS_MODE)"
if [[ $INSPECT -eq 1 ]]; then
  if (( BLOCKERS > 0 )); then
    say "Inspección terminada: no se cambió nada. Hay $BLOCKERS problema(s) que bloquean la instalación (en rojo, arriba): mandame este reporte."
    exit 1
  fi
  say "Inspección terminada: no se cambió nada. No hay bloqueos: podés correr la instalación completa."
  exit 0
fi

# ---------------------------------------------------------------- 2. Paquetes
say "Instalando lo que falte"
export DEBIAN_FRONTEND=noninteractive
# En una VPS recién creada, cloud-init o las actualizaciones automáticas pueden tener tomado apt varios minutos.
wait_apt() {
  if command -v cloud-init >/dev/null; then timeout 900 cloud-init status --wait >/dev/null 2>&1 || true; fi
  python3 - <<'PY' || die "apt está ocupado hace más de 15 minutos (¿actualizaciones automáticas?). Probá de nuevo más tarde."
import fcntl, os, sys, time
end = time.time() + 900
for p in ("/var/lib/dpkg/lock-frontend", "/var/lib/dpkg/lock", "/var/lib/apt/lists/lock"):
    if not os.path.exists(p):
        continue
    fd = os.open(p, os.O_RDWR)
    while True:
        try:
            fcntl.lockf(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            fcntl.lockf(fd, fcntl.LOCK_UN)
            break
        except OSError:
            if time.time() > end:
                sys.exit(1)
            time.sleep(5)
    os.close(fd)
PY
}
APT=(apt-get -o DPkg::Lock::Timeout=600 -y -qq)
apt_update() {
  wait_apt
  apt-get -o DPkg::Lock::Timeout=600 update -qq \
    || warn "apt-get update terminó con errores (¿repositorios de terceros vencidos?). Sigo con los índices disponibles."
}
apt_update
"${APT[@]}" install ca-certificates curl git openssl python3 >/dev/null
if ! command -v docker >/dev/null; then
  # Repositorio oficial de Docker (Engine + Compose v2 + buildx con versiones al día).
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL "https://download.docker.com/linux/$ID/gpg" -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/$ID ${VERSION_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt_update
  # --no-remove: si algo de otro proyecto tuviera que desinstalarse, apt se detiene en lugar de hacerlo en silencio.
  "${APT[@]}" install --no-remove docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin >/dev/null
  systemctl enable --now docker
fi
systemctl is-active --quiet docker || systemctl enable --now docker
docker compose version >/dev/null 2>&1 || "${APT[@]}" install --no-remove docker-compose-plugin >/dev/null 2>&1 \
  || "${APT[@]}" install --no-remove docker-compose-v2 >/dev/null || die "No se pudo instalar Docker Compose v2."
docker buildx version >/dev/null 2>&1 || "${APT[@]}" install --no-remove docker-buildx-plugin >/dev/null 2>&1 \
  || "${APT[@]}" install --no-remove docker-buildx >/dev/null || die "No se pudo instalar docker buildx."
if [[ $HOST_NGINX -eq 0 ]]; then
  if python3 -c 'import socket; socket.socket(socket.AF_INET6).close()' 2>/dev/null; then
    "${APT[@]}" install --no-remove nginx >/dev/null
  else
    # Kernel sin IPv6: el sitio default del paquete escucha en [::]:80 y su arranque haría fallar la instalación.
    # Se instala sin arrancar (policy-rc.d temporal), se comenta esa línea y recién ahí se arranca.
    if [[ ! -e /usr/sbin/policy-rc.d ]]; then printf '#!/bin/sh\nexit 101\n' > /usr/sbin/policy-rc.d; chmod 755 /usr/sbin/policy-rc.d; POLICY_CREATED=1; fi
    "${APT[@]}" install --no-remove nginx >/dev/null
    if [[ $POLICY_CREATED -eq 1 ]]; then rm -f /usr/sbin/policy-rc.d; POLICY_CREATED=0; fi
    sed -i -E 's/^([[:space:]]*listen[[:space:]]+\[::\])/#\1/' /etc/nginx/sites-available/default 2>/dev/null || true
  fi
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
# «listen [::]» puerto por puerto, solo donde Nginx ya escucha en IPv6: si no, este sitio pasaría a ser el default
# implícito de IPv6 y los sitios ajenos con AAAA recibirían su certificado en lugar de volver a IPv4.
NO_IPV6=1; NO_IPV6_443=1
if [[ $HAS_V6 -eq 1 && "$TLS_MODE" != ip ]]; then
  v6_listen="$(ss -Htln 2>/dev/null | awk '{print $4}')"
  grep -qx '\[::\]:80' <<<"$v6_listen" && NO_IPV6=""
  grep -qx '\[::\]:443' <<<"$v6_listen" && NO_IPV6_443=""
fi
export NO_IPV6 NO_IPV6_443
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
