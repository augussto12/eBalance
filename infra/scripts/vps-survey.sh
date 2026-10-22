#!/usr/bin/env bash
# Relevamiento de SOLO LECTURA de una VPS con otros proyectos: qué corre, cómo está conectado y qué recursos quedan.
# No instala, no reinicia, no recarga ni modifica configuración, contenedores ni logs. Lo único que escribe es el reporte
# (modo 600) en un archivo temporal. NO muestra variables de entorno (salvo los nombres de host de ruteo), contenidos de
# .env/compose, comandos completos de cron ni claves; además, todo pasa por un filtro (perl) que tapa contraseñas,
# tokens, hashes y credenciales en URLs. En contenedores de proxy (nginx/NPM/Caddy) solo ejecuta lectura de configuración.
#
#   curl -fsSL https://raw.githubusercontent.com/augussto12/eBalance/implementacion-v1/infra/scripts/vps-survey.sh -o vps-survey.sh
#   less vps-survey.sh            # revisalo
#   sudo bash vps-survey.sh       # imprime el reporte y lo guarda en /tmp/relevamiento-vps.XXXXXX.txt
set -uo pipefail
export LC_ALL=C

[[ $EUID -eq 0 ]] || { echo "Corré el relevamiento como root (sudo bash vps-survey.sh)." >&2; exit 1; }
umask 077
REPORT="$(mktemp /tmp/relevamiento-vps.XXXXXX.txt)"
T=20          # segundos máximos por comando
MAXC=60       # contenedores detallados como máximo

# Filtro de redacción (perl-base es esencial en Debian/Ubuntu). Orden: formatos conocidos → claves sensibles → largas.
REDACT_PL='BEGIN { $| = 1 }
s{(\b[a-z][a-z0-9+.-]*://)[^/@\s"'"'"'<>]*@}{$1***@}gi;                                    # usuario:clave@ / token@ en URLs
s{\b((?:proxy-)?authorization)(["'"'"']?\s*[:=]\s*)[^"'"'"'\n]*}{$1$2<redactado>}gi;        # Authorization: Basic|Bearer …
s{-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*}{<redactado-clave-privada>}g;
s{\beyJ[\w-]{8,}\.[\w-]{8,}\.[\w-]{8,}}{<redactado-jwt>}g;
s{\b(?:AKIA|ASIA)[0-9A-Z]{16}\b}{<redactado-aws>}g;
s{\b(?:gh[pousr]_[A-Za-z0-9]{16,}|github_pat_\w{20,}|glpat-[\w-]{16,}|xox[abprs]-[\w-]{10,}|(?:sk|rk|pk)_(?:live|test)_\w{10,}|AIza[\w-]{30,}|npm_[A-Za-z0-9]{30,})}{<redactado-token>}g;
s{\b\d{8,10}:[\w-]{30,}\b}{<redactado-token>}g;                                            # bots de Telegram
s{(hooks\.slack\.com/services|discord(?:app)?\.com/api/webhooks)/\S+}{$1/<redactado>}gi;
s{\$(?:apr1|2[abxy]|5|6|y|argon2\w*)\$[^\s"'"'"',;]+}{<redactado-hash>}g;                   # htpasswd / bcrypt / crypt
s{(\s-u\s+|\s--user[ =])[^\s:]+:\S+}{$1<redactado>}g;                                       # curl -u usuario:clave
s{(\s--?(?:password|passwd|pass|pwd|token|secret|api-?key|auth-?token|access-?key)\b)(\s+|=)(?!-)\S+}{$1$2<redactado>}gi;
s{(\bmysql(?:dump|admin)?\b[^\n]*?\s-p)(?!\s)\S+}{$1<redactado>}g;
s{(\bsshpass\s+-p\s*)\S+}{$1<redactado>}g;
s{\b([\w.-]*(?:pass(?:word|wd)?|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|credentials?|salt|cookie|dsn|webhook|basic_?auth[\w.-]*|digest_?auth[\w.-]*)[\w.-]*)(["'"'"']?\s*[:=]\s*["'"'"']?)[^"'"'"'\s;]+}{$1$2<redactado>}gi;
s{\b(?=[A-Za-z0-9+]*[0-9])(?=[A-Za-z0-9+]*[A-Za-z])[A-Za-z0-9+]{32,}={0,2}}{<redactado-largo>}g;'
redact() { perl -pe "$REDACT_PL"; }

section() { printf '\n==================== %s ====================\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }
# Ejecuta con tiempo máximo; los errores se muestran pero no detienen el relevamiento.
# --foreground: Ctrl-C llega también al comando en curso (sin eso, timeout lo aísla de la terminal).
run() { timeout --foreground -k 5 "$T" "$@" 2>&1 || echo "(sin datos: «$*» terminó con código $?)"; }
dk() { timeout --foreground -k 5 "$T" docker "$@"; }
nicely() { nice -n 19 ionice -c3 "$@" 2>/dev/null || nice -n 19 "$@"; }
# Proceso dueño de un puerto TCP; si es un contenedor con red del host, su nombre.
owner_of_port() {
  local line pid name cid cname
  line="$(ss -Htlnp "sport = :$1" 2>/dev/null | head -1)"
  [[ -n "$line" ]] || { echo "libre"; return; }
  name="$(grep -o 'users:(("[^"]*"' <<<"$line" | head -1 | cut -d'"' -f2)"
  pid="$(grep -o 'pid=[0-9]*' <<<"$line" | head -1 | cut -d= -f2)"
  cid="$(grep -oE '[0-9a-f]{64}' "/proc/${pid:-0}/cgroup" 2>/dev/null | head -1)"
  if [[ -n "$cid" && $DOCKER_OK -eq 1 ]]; then
    cname="$(dk inspect -f '{{.Name}}' "$cid" 2>/dev/null)"
    echo "${name:-?} (contenedor ${cname#/} con red del host)"
  elif [[ "$name" == docker-proxy && $DOCKER_OK -eq 1 ]]; then
    echo "docker-proxy (contenedor $(dk ps --filter "publish=$1" --format '{{.Names}}' 2>/dev/null | tr '\n' ' '))"
  else
    echo "${name:-?}"
  fi
}
# ¿Se superponen dos rangos IPv4 CIDR? (sin python, solo aritmética de bash)
ip2int() { local IFS=.; read -r a b c d <<<"$1"; echo $(( (a << 24) + (b << 16) + (c << 8) + d )); }
cidr_overlap() {
  local n1="${1%/*}" p1="${1#*/}" n2="${2%/*}" p2="${2#*/}" m
  [[ "$1" == */* && "$2" == */* && "$n1" =~ ^[0-9.]+$ && "$n2" =~ ^[0-9.]+$ ]] || return 1
  m=$(( p1 < p2 ? p1 : p2 ))
  (( m == 0 )) && return 0
  (( ( $(ip2int "$n1") >> (32 - m) ) == ( $(ip2int "$n2") >> (32 - m) ) ))
}

DOCKER_OK=0
# «docker info» puede responder aunque «docker ps» quede colgado (lock de un contenedor, volumen NFS): se prueba ps.
if have docker && timeout --foreground -k 2 10 docker ps -q >/dev/null 2>&1; then DOCKER_OK=1; fi

system() {
  echo "Relevamiento de solo lectura · $(date -u +%FT%TZ) · $(hostname -f 2>/dev/null || hostname)"
  section "1. Sistema"
  local pretty="?"; [[ -r /etc/os-release ]] && pretty="$(. /etc/os-release; echo "${PRETTY_NAME:-?}")"
  echo "SO: $pretty · kernel $(uname -r) · $(uname -m) · virtualización: $(systemd-detect-virt 2>/dev/null || echo '?')"
  echo "CPU: $(nproc) · carga: $(cut -d' ' -f1-3 /proc/loadavg) · encendida: $(uptime -p 2>/dev/null || echo '?')"
  awk '/^(MemTotal|MemAvailable|SwapTotal|SwapFree):/{printf "%s %d MiB  ", $1, $2/1024} END{print ""}' /proc/meminfo
  run df -hT -x tmpfs -x devtmpfs -x overlay -x squashfs -x efivarfs
  echo "Zona horaria: $(timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null || echo '?')"
}

network() {
  section "2. Red"
  run ip -br addr
  run ip -4 route
  echo "IPv6 global: $(ip -6 addr show scope global 2>/dev/null | grep -c inet6) dirección(es) · IPv6 en el kernel: $([[ -e /proc/net/if_inet6 ]] && echo sí || echo no)"
  echo "Resolvers: $(awk '/^nameserver/{print $2}' /etc/resolv.conf 2>/dev/null | tr '\n' ' ')"
  echo "IP pública (vista desde afuera): $(curl -4 -fsS --max-time 6 https://api.ipify.org 2>/dev/null || echo '?')"

  section "3. Puertos escuchando"
  echo "--- TCP (dirección local, proceso):"
  ss -Htlnp 2>/dev/null | awk '{p=$6; sub(/^users:\(\("/,"",p); sub(/".*/,"",p); print "  " $4 "  " p}' | sort -u
  echo "--- UDP:"; ss -Hulnp 2>/dev/null | awk '{p=$6; sub(/^users:\(\("/,"",p); sub(/".*/,"",p); print "  " $4 "  " p}' | sort -u | head -20
  echo "--- Dueño de 80: $(owner_of_port 80) · de 443: $(owner_of_port 443)"
  local exposed docker_pub
  exposed="$(ss -Htln 2>/dev/null | awk '{print $4}' | grep -vE '^(127\.|\[::1\]|\[::ffff:127\.)' | sed -E 's/.*:([0-9]+)$/\1/' | sort -nu | tr '\n' ' ')"
  docker_pub=""
  [[ $DOCKER_OK -eq 1 ]] && docker_pub="$(dk ps --format '{{.Ports}}' 2>/dev/null | grep -oE '(0\.0\.0\.0|\[::\]|::|[1-9][0-9.]*):[0-9]+->' | grep -vE '^127\.' | grep -oE ':[0-9]+->' | tr -d ':>-' | sort -nu | tr '\n' ' ')"
  echo "--- Accesibles desde fuera de la VPS (si el firewall lo permite): $exposed"
  echo "--- Publicados por Docker (pueden no figurar en ss): ${docker_pub:-ninguno}"
  echo "--- Puertos típicos de bases de datos o paneles accesibles desde afuera:"
  local p hits=""
  for p in $exposed $docker_pub; do
    case "$p" in 5432|3306|33060|6379|27017|9200|5984|11211|2375|2376|9000|9443|8000|8080|81|2019|3000|10000|5601|15672|8443|7800|8090|8880|8888|19999|61208) hits+=" $p";; esac
  done
  hits="$(xargs -n1 <<<"$hits" | sort -nu | xargs)"
  echo "  ${hits:-(ninguno de los típicos)}"
}

firewall() {
  section "4. Firewall"
  if have ufw; then run ufw status verbose; else echo "ufw no instalado"; fi
  if have nft; then echo "--- nftables (reglas de input/forward, resumido):"; timeout --foreground "$T" nft list ruleset 2>/dev/null | grep -E '^\s*(table|chain|type filter)|dport|policy' | head -50; fi
  echo "--- iptables INPUT:"; iptables -S INPUT 2>/dev/null | head -30
  echo "--- ip6tables INPUT:"; ip6tables -S INPUT 2>/dev/null | head -15
  echo "--- iptables DOCKER-USER:"; iptables -S DOCKER-USER 2>/dev/null | head -15
  if have fail2ban-client; then run fail2ban-client status; fi
  echo "Recordatorio: el firewall del PANEL del proveedor (hPanel) no se ve desde acá."

  section "5. SSH (configuración efectiva, sin claves)"
  if have sshd; then timeout --foreground "$T" sshd -T 2>/dev/null | grep -Ei '^(port|permitrootlogin|passwordauthentication|pubkeyauthentication|kbdinteractiveauthentication|allowusers|allowgroups) '; fi
  echo "Claves autorizadas de root: $(grep -cvE '^[[:space:]]*(#|$)' /root/.ssh/authorized_keys 2>/dev/null || echo 0)"
}

panels() {
  section "6. Paneles y orquestadores"
  local p found=0
  for p in /data/coolify /etc/dokploy /etc/easypanel /usr/local/hestia /usr/local/cpanel /usr/local/CyberCP /www/server/panel \
           /usr/local/lsws /home/clp /usr/local/psa /etc/webmin /usr/local/fastpanel2 /usr/local/ispconfig /opt/1panel /etc/casaos \
           /captain /var/lib/caprover /opt/portainer /opt/nginx-proxy-manager /usr/local/directadmin /docker; do
    [[ -e "$p" ]] && { echo "Encontrado: $p"; found=1; }
  done
  [[ $found -eq 1 ]] || echo "(ningún directorio de panel conocido)"
  if [[ $DOCKER_OK -eq 1 ]]; then
    echo "Swarm: $(dk info --format '{{.Swarm.LocalNodeState}}' 2>/dev/null)"
    if [[ "$(dk info --format '{{.Swarm.LocalNodeState}}' 2>/dev/null)" == active ]]; then
      echo "--- Servicios de Swarm:"; dk service ls --format '  {{.Name}} {{.Image}} {{.Replicas}} {{.Ports}}' 2>&1 | head -40
      echo "--- Stacks:"; dk stack ls 2>&1 | head -20
    fi
  fi
}

docker_engine() {
  section "7. Docker: motor"
  if ! have docker; then echo "Docker no instalado"; return; fi
  if [[ $DOCKER_OK -eq 0 ]]; then echo "Docker instalado pero el daemon no responde a «docker ps» en 10 s (se omiten las secciones de Docker)."; return; fi
  echo "Binario: $(readlink -f "$(command -v docker)")"
  dk version --format 'Cliente {{.Client.Version}} · Servidor {{.Server.Version}} · API {{.Server.APIVersion}}' 2>&1
  echo "Compose: $(dk compose version --short 2>/dev/null || echo 'no (v2)') · docker-compose v1: $(command -v docker-compose >/dev/null && docker-compose version --short 2>/dev/null || echo no) · buildx: $(dk buildx version 2>/dev/null | awk '{print $2}' || echo no)"
  dk info --format 'Driver: {{.Driver}} · Raíz: {{.DockerRootDir}} · Logs: {{.LoggingDriver}} · cgroup: {{.CgroupDriver}} v{{.CgroupVersion}} · Contenedores: {{.Containers}} ({{.ContainersRunning}} corriendo) · Imágenes: {{.Images}} · Live restore: {{.LiveRestoreEnabled}}' 2>&1 | head -2
  echo "--- /etc/docker/daemon.json:"; cat /etc/docker/daemon.json 2>/dev/null || echo "(no existe)"
  echo "--- Argumentos de dockerd:"; ps -o args= -C dockerd 2>/dev/null | head -2
  echo "--- Espacio:"; dk system df 2>&1
}

containers() {
  [[ $DOCKER_OK -eq 1 ]] || return 0
  section "8. Contenedores (sin variables de entorno)"
  local ids n id
  ids="$(dk ps -aq 2>/dev/null)"
  [[ -n "$ids" ]] || { echo "(no hay contenedores)"; return 0; }
  n="$(wc -l <<<"$ids")"
  (( n > MAXC )) && echo "(hay $n contenedores; se detallan los primeros $MAXC)"
  # Plantilla en modo «JSON crudo» (estable entre versiones): las claves opcionales se leen con index.
  # shellcheck disable=SC2016
  local tpl='
--- {{.Name}}  [{{.State.Status}}{{with index .State "Health"}} · salud: {{index . "Status"}}{{end}} · reinicios: {{.RestartCount}}]
    imagen: {{.Config.Image}}
    compose: {{with .Config.Labels}}proyecto={{or (index . "com.docker.compose.project") "-"}} servicio={{or (index . "com.docker.compose.service") "-"}} dir={{or (index . "com.docker.compose.project.working_dir") "-"}}{{end}}
    reinicio: {{.HostConfig.RestartPolicy.Name}} · red: {{.HostConfig.NetworkMode}} · privilegiado: {{.HostConfig.Privileged}} · usuario: "{{.Config.User}}" · memoria máx: {{.HostConfig.Memory}} · cpus(nano): {{index .HostConfig "NanoCpus"}}
    puertos publicados: {{range $p, $b := .HostConfig.PortBindings}}{{range $b}}{{with index . "HostIp"}}{{.}}{{else}}0.0.0.0{{end}}:{{index . "HostPort"}}->{{$p}} {{end}}{{end}}
    redes: {{range $n, $c := .NetworkSettings.Networks}}{{$n}}({{index $c "IPAddress"}}) {{end}}
    montajes: {{range .Mounts}}{{index . "Type"}}:{{with index . "Name"}}{{.}}{{else}}{{index . "Source"}}{{end}}->{{index . "Destination"}}{{if not (index . "RW")}}(ro){{end}} {{end}}
    ruteo: {{range $k, $v := .Config.Labels}}{{if or (eq (printf "%.7s" $k) "traefik") (eq (printf "%.5s" $k) "caddy")}}{{$k}}={{$v}} {{end}}{{end}}{{range .Config.Env}}{{if or (eq (printf "%.13s" .) "VIRTUAL_HOST=") (eq (printf "%.13s" .) "VIRTUAL_PORT=") (eq (printf "%.17s" .) "LETSENCRYPT_HOST=")}}{{.}} {{end}}{{end}}'
  for id in $(head -"$MAXC" <<<"$ids"); do
    dk inspect -f "$tpl" "$id" 2>/dev/null || echo "--- $(dk ps -a --filter "id=$id" --format '{{.Names}}'): docker inspect falló"
  done
  echo
  echo "--- docker.sock montado en:"
  for id in $ids; do
    dk inspect -f '{{.Name}}{{range .Mounts}}{{if or (eq (index . "Source") "/var/run/docker.sock") (eq (index . "Source") "/run/docker.sock")}} ← docker.sock{{end}}{{end}}' "$id" 2>/dev/null
  done | grep 'docker.sock' || echo "(ninguno)"
  echo "--- Uso actual de recursos:"; dk stats --no-stream --format '{{.Name}}  CPU {{.CPUPerc}}  MEM {{.MemUsage}}' 2>&1 | head -"$MAXC"
}

projects() {
  [[ $DOCKER_OK -eq 1 ]] || return 0
  section "9. Proyectos de Docker Compose (nombres de archivos; contenidos NO)"
  dk compose ls -a 2>/dev/null || echo "(docker compose ls no disponible)"
  local dirs d
  dirs="$(dk ps -a --format '{{.Label "com.docker.compose.project.working_dir"}}' 2>/dev/null | sort -u | grep -v '^$' | head -30)"
  while IFS= read -r d; do
    [[ -n "$d" ]] || continue
    echo "--- $d"
    if [[ -d "$d" ]]; then
      echo "    archivos: $(ls -1A "$d" 2>/dev/null | head -25 | tr '\n' ' ')"
      [[ -f "$d/.env" ]] && echo "    tiene .env (no se muestra)"
      if git -c safe.directory='*' -C "$d" rev-parse --git-dir >/dev/null 2>&1; then
        echo "    git: $(git -c safe.directory='*' -C "$d" remote get-url origin 2>/dev/null) · rama $(git -c safe.directory='*' -C "$d" rev-parse --abbrev-ref HEAD 2>/dev/null) · $(git -c safe.directory='*' -C "$d" log -1 --format='%h %cs' 2>/dev/null)"
      fi
    else
      echo "    (el directorio ya no existe en el host)"
    fi
  done <<<"$dirs"
  echo "--- Contenedores sin Compose (docker run):"
  dk ps -a --format '{{.Names}} {{.Label "com.docker.compose.project"}}' 2>/dev/null | awk 'NF==1{print "    " $1; n++} END{if(!n) print "    (ninguno)"}'
}

networks_volumes() {
  [[ $DOCKER_OK -eq 1 ]] || return 0
  section "10. Redes y volúmenes de Docker"
  dk network ls -q 2>/dev/null | head -80 | xargs -r timeout --foreground -k 5 "$T" docker network inspect \
    -f '{{.Name}} · {{.Driver}} · interna={{.Internal}} · subred={{range .IPAM.Config}}{{index . "Subnet"}} {{end}}· contenedores: {{range .Containers}}{{.Name}} {{end}}' 2>&1
  echo "--- default-address-pools: $(grep -A6 'default-address-pools' /etc/docker/daemon.json 2>/dev/null | tr -d ' \n' || echo 'por defecto')"
  echo "--- Volúmenes (nombre, proyecto):"
  dk volume ls --format '{{.Name}} {{.Label "com.docker.compose.project"}}' 2>/dev/null | awk '{printf "  %s %s\n", (length($1)>40 ? "<anónimo>" : $1), $2}' | sort | uniq -c | head -60
  echo "--- Imágenes (repositorio:tag, tamaño):"; dk image ls --format '{{.Repository}}:{{.Tag}}  {{.Size}}' 2>/dev/null | head -50
}

# Resumen de un «nginx -T» (stdin): por bloque server/upstream, archivo + listen, server_name, destino y certificado.
summarize_nginx() {
  perl -ne '
    BEGIN { $| = 1; $depth = 0; $blk = 0 }
    if (/^# configuration file (.+):$/) { $file = $1; next }
    s/#.*//; my $line = $_;
    if (!$blk && $line =~ /^\s*(server|upstream\s+\S+)\s*\{/) { $blk = $depth + 1; print "\n  [$file] $1\n" }
    elsif ($blk && $line =~ /^\s*(listen|server_name|proxy_pass|fastcgi_pass|uwsgi_pass|grpc_pass|root|alias|return|ssl_certificate|server|location|set \$(?:server|port|forward_scheme))\b/) {
      (my $d = $line) =~ s/^\s+|\s*\{?\s*$//g; print "    $d\n";
    }
    $depth += () = $line =~ /\{/g; $depth -= () = $line =~ /\}/g;
    $blk = 0 if $blk && $depth < $blk;
  ' | head -"${1:-300}"
}

web() {
  section "11. Servidores web y proxys del HOST"
  local s
  for s in nginx apache2 httpd caddy haproxy traefik lighttpd openresty lsws; do
    systemctl is-active --quiet "$s" 2>/dev/null && echo "Servicio activo: $s ($(systemctl is-enabled "$s" 2>/dev/null))"
  done
  if have nginx; then
    echo "nginx: $(nginx -v 2>&1 | cut -d/ -f2)"
    echo "--- Sitios habilitados:"
    local f
    for f in /etc/nginx/sites-enabled/* /etc/nginx/conf.d/*; do
      if [[ -L "$f" ]]; then echo "  $f → $(readlink "$f")"; elif [[ -e "$f" ]]; then echo "  $f"; fi
    done
    echo "--- nginx -t:"; run nginx -t
    echo "--- Bloques (archivo, listen, server_name, destino, certificado):"; timeout --foreground "$T" nginx -T 2>/dev/null | summarize_nginx 300
  fi
  if have apache2ctl; then echo "--- Apache:"; run apache2ctl -S; fi
  if [[ -d /etc/caddy ]]; then
    echo "--- Caddy (host): sitios y destinos (incluye archivos importados):"
    find /etc/caddy -maxdepth 3 -type f 2>/dev/null | head -40 | xargs -r grep -HE '^[^[:space:]#].*\{|reverse_proxy|import' 2>/dev/null | head -80
  fi
  if [[ -f /etc/haproxy/haproxy.cfg ]]; then
    echo "--- HAProxy (host):"; grep -E '^\s*(frontend|backend|listen|bind|server|use_backend|default_backend)\b' /etc/haproxy/haproxy.cfg | head -60
  fi
  local ip; ip="$(curl -4 -fsS --max-time 6 https://api.ipify.org 2>/dev/null || true)"
  echo "--- Qué responde en 80/443 (local y por la IP pública ${ip:-?}):"
  local u
  for u in http://127.0.0.1/ https://127.0.0.1/ ${ip:+http://$ip/ https://$ip/}; do
    echo "    $u → $(curl -sk -o /dev/null --max-time 5 -w '%{http_code} %{redirect_url}' "$u" 2>/dev/null) · $(curl -skI --max-time 5 "$u" 2>/dev/null | grep -i '^server:' | tr -d '\r')"
  done
  echo "--- Qué sirve cada puerto TCP accesible desde afuera (código HTTP y título):"
  local port out code title
  for port in $(ss -Htln 2>/dev/null | awk '{print $4}' | grep -vE '^(127\.|\[::1\])' | sed -E 's/.*:([0-9]+)$/\1/' | sort -nu | head -40); do
    [[ "$port" == 22 ]] && continue
    out="$(curl -sk --max-time 3 "http://127.0.0.1:$port/" 2>/dev/null | head -c 20000)"
    code="$(curl -sk -o /dev/null --max-time 3 -w '%{http_code}' "http://127.0.0.1:$port/" 2>/dev/null)"
    title="$(grep -oiE '<title>[^<]{0,80}' <<<"$out" | head -1 | sed 's/<[tT][iI][tT][lL][eE]>//')"
    echo "    :$port → HTTP ${code:-?} ${title:+«$title»}"
  done
}

container_proxies() {
  [[ $DOCKER_OK -eq 1 ]] || return 0
  section "12. Proxys en contenedores (solo lectura de su configuración)"
  local c any=0
  for c in $(dk ps --format '{{.Names}} {{.Image}}' 2>/dev/null | grep -Ei 'nginx|openresty|proxy-manager|jc21' | awk '{print $1}' | head -10); do
    any=1; echo "  [$c] nginx -T resumido:"
    timeout --foreground -k 5 "$T" docker exec "$c" nginx -T 2>/dev/null | summarize_nginx 150
  done
  for c in $(dk ps --format '{{.Names}} {{.Image}}' 2>/dev/null | grep -Ei 'traefik' | awk '{print $1}' | head -5); do
    any=1; echo "  [$c] argumentos de Traefik:"
    dk inspect -f '{{range .Args}}{{.}}{{"\n"}}{{end}}' "$c" 2>/dev/null | sort -u | sed 's/^/    /' | head -40
    echo "  [$c] montajes (configuración dinámica):"; dk inspect -f '{{range .Mounts}}{{index . "Source"}}->{{index . "Destination"}}{{"\n"}}{{end}}' "$c" 2>/dev/null | sed 's/^/    /'
    local src f2
    for src in $(dk inspect -f '{{range .Mounts}}{{if eq (index . "Type") "bind"}}{{index . "Source"}} {{end}}{{end}}' "$c" 2>/dev/null); do
      [[ "$src" == */docker.sock ]] && continue
      for f2 in $(find "$src" -maxdepth 3 -type f \( -name '*.yml' -o -name '*.yaml' -o -name '*.toml' \) 2>/dev/null | head -40); do
        grep -hoE 'Host(SNI)?\(`[^`]+`\)|url: *"?[a-z]+://[^" ]+|address: *"?:[0-9]+' "$f2" 2>/dev/null | sort -u | sed "s#^#    $f2: #"
      done | head -80
    done
  done
  for c in $(dk ps --format '{{.Names}} {{.Image}}' 2>/dev/null | grep -Ei 'caddy' | awk '{print $1}' | head -5); do
    any=1; echo "  [$c] Caddyfile (sitios y destinos):"
    timeout --foreground -k 5 "$T" docker exec "$c" sh -c 'find /etc/caddy -maxdepth 3 -type f | head -40 | xargs grep -HE "^[^[:space:]#].*\{|reverse_proxy|import"' 2>/dev/null | head -60 | sed 's/^/    /'
  done
  local d f
  for d in /data/coolify/proxy /etc/dokploy/traefik /etc/easypanel/traefik /opt/traefik /etc/traefik; do
    [[ -d "$d" ]] || continue
    any=1; echo "  [$d] reglas de Traefik por archivo (Host y destinos):"
    for f in $(find "$d" -maxdepth 4 -type f \( -name '*.yml' -o -name '*.yaml' -o -name '*.toml' \) 2>/dev/null | head -60); do
      grep -hoE 'Host(SNI)?\(`[^`]+`\)|url: *"?[a-z]+://[^" ]+|entryPoints?:.*|address: *"?:[0-9]+' "$f" 2>/dev/null | sort -u | sed "s#^#    $(basename "$f"): #"
    done | head -120
  done
  [[ $any -eq 1 ]] || echo "(no hay contenedores de proxy conocidos)"
}

tls() {
  section "13. Certificados"
  local d
  for d in /etc/letsencrypt/live/*/ /etc/alvarez-finanzas/letsencrypt/live/*/; do
    [[ -f "$d/cert.pem" ]] || continue
    echo "  $d: $(openssl x509 -in "$d/cert.pem" -noout -enddate -ext subjectAltName 2>/dev/null | tr '\n' ' ' | tr -s ' ')"
  done
  echo "--- Renovación de certbot (authenticator por certificado):"; grep -H '^authenticator' /etc/letsencrypt/renewal/*.conf 2>/dev/null || echo "  (sin certbot del sistema)"
  echo "--- Otros clientes ACME:"; for d in /root/.acme.sh /etc/caddy /var/lib/caddy /data/coolify/proxy/acme.json /etc/dokploy/traefik/dynamic/acme.json; do [[ -e "$d" ]] && echo "  $d"; done
  if [[ $DOCKER_OK -eq 1 ]]; then
    local id
    for id in $(dk ps -q 2>/dev/null | head -"$MAXC"); do
      dk inspect -f '{{.Name}}: {{range .Mounts}}{{index . "Source"}}->{{index . "Destination"}} {{end}}' "$id" 2>/dev/null
    done | grep -iE 'acme|letsencrypt|certs|ssl' | sed 's/^/  contenedor /'
  fi
  echo "--- Certificado que se sirve realmente por nombre (443 local):"
  local names n
  names="$( { have nginx && timeout --foreground "$T" nginx -T 2>/dev/null | awk '$1=="server_name"{for(i=2;i<=NF;i++){gsub(/;/,"",$i); print $i}}'; } | grep -vE '^(_|localhost|\*.*|~.*)$' | sort -u | head -25)"
  for n in $names; do
    echo "  $n → $(timeout --foreground 6 openssl s_client -connect 127.0.0.1:443 -servername "$n" </dev/null 2>/dev/null | openssl x509 -noout -issuer -enddate 2>/dev/null | tr '\n' ' ' || echo 'sin TLS')"
  done
}

host_services() {
  section "14. Servicios, timers y tareas programadas"
  echo "--- Servicios en ejecución (excepto los de base del sistema):"
  systemctl list-units --type=service --state=running --no-legend --plain 2>/dev/null | awk '{print $1}' \
    | grep -vE '^(systemd-|dbus|cron|rsyslog|ssh|getty|serial-getty|polkit|networkd-dispatcher|unattended-upgrades|udisks2|ModemManager|multipathd|snapd|qemu-guest-agent|user@|accounts-daemon|irqbalance|packagekit|containerd|docker|chrony|ntp|atd|haveged|rngd)' | sed 's/^/  /'
  echo "--- Unidades fallidas:"; systemctl --failed --no-legend --plain 2>/dev/null | awk '{print "  " $1}'
  echo "--- Timers (unidad → activa):"; systemctl list-timers --all --no-legend --plain 2>/dev/null | awk '{print "  " $(NF-1) " → " $NF}' | head -30
  echo "--- cron (horario · usuario · programa · rutas; sin argumentos):"
  local f
  {
    for f in /etc/crontab /etc/cron.d/*; do [[ -f "$f" ]] && awk -v src="$f" '{print src "\t" $0}' "$f"; done
    for f in /var/spool/cron/crontabs/*; do [[ -f "$f" ]] && awk -v src="$f" '{print src "\tUSER " $0}' "$f"; done
  } 2>/dev/null | perl -F'\t' -lane '
      my ($src, $l) = ($F[0], join("\t", @F[1..$#F]));
      my $user_tab = ($l =~ s/^USER //);
      next if $l =~ /^\s*(#|$)/ || $l =~ /^\s*[A-Z_]+=/;
      my @w = split " ", $l;
      my ($when, $user, @cmd);
      if ($w[0] =~ /^@/) { $when = shift @w } else { $when = join(" ", splice(@w, 0, 5)) }
      $user = $user_tab ? (split m{/}, $src)[-1] : shift @w;
      my $cmd = join(" ", @w);
      my ($prog) = $cmd =~ m{^(?:\S+=\S+\s+)*(\S+)}; $prog //= "?"; $prog =~ s{.*/}{};
      my %seen; my @paths = grep { !$seen{$_}++ } ($cmd =~ m{(?<![:/\w])(/[\w.-][\w./-]*)}g);
      printf "  %-16s %-8s %-14s %s  (%s)\n", $when, $user, $prog, join(" ", @paths[0..($#paths < 2 ? $#paths : 2)]), $src;
    ' | head -50
  echo "--- Scripts en cron.hourly/daily/weekly/monthly:"; ls /etc/cron.hourly /etc/cron.daily /etc/cron.weekly /etc/cron.monthly 2>/dev/null | tr '\n' ' '; echo
  echo "--- Procesos que más memoria usan:"; ps -eo rss,comm --sort=-rss 2>/dev/null | awk 'NR>1 && NR<=13 {printf "  %5d MiB %s\n", $1/1024, $2}'
}

layout() {
  section "15. Dónde viven los proyectos (tamaños de primer nivel)"
  local d
  for d in /srv /opt /var/www /home /root /data /apps /docker; do
    [[ -d "$d" ]] || continue
    echo "--- $d"; nicely timeout --foreground "$T" du -xsh "$d"/* 2>/dev/null | sort -rh | head -12 | sed 's/^/  /'
  done
}

summary() {
  section "16. Resumen para instalar Álvarez Finanzas"
  echo "Puerto 80: $(owner_of_port 80) · Puerto 443: $(owner_of_port 443)"
  if [[ $DOCKER_OK -eq 1 ]]; then
    echo "Contenedores que publican 80/443: $(dk ps --format '{{.Names}} {{.Ports}}' 2>/dev/null | grep -E '(^|[ ,:])(80|443)->' | awk '{print $1}' | tr '\n' ' ')"
    local nets n busy=""
    nets="$(dk network ls -q 2>/dev/null | xargs -r timeout --foreground -k 5 "$T" docker network inspect -f '{{.Name}} {{range .IPAM.Config}}{{index . "Subnet"}} {{end}}' 2>/dev/null)"
    while read -r name rest; do
      for n in $rest; do [[ "$name" != alvarez-finanzas_* ]] && cidr_overlap "$n" 172.30.57.0/24 && busy+=" $name($n)"; done
    done <<<"$nets"
    for n in $(ip -4 route 2>/dev/null | awk '$1 ~ /\//{print $1}'); do cidr_overlap "$n" 172.30.57.0/24 && ! grep -q "$n" <<<"$nets" && busy+=" ruta($n)"; done
    echo "¿172.30.57.0/24 libre? $([[ -z "$busy" ]] && echo sí || echo "NO:$busy")"
  fi
  echo "¿127.0.0.1:8088 libre? $(ss -Htln 'sport = :8088' 2>/dev/null | grep -q . && echo "NO ($(owner_of_port 8088))" || echo sí)"
  echo "Memoria disponible: $(awk '/^MemAvailable:/{a=$2} /^SwapFree:/{s=$2} END{printf "%d MiB (RAM+swap libres)", (a+s)/1024}' /proc/meminfo) · Disco libre en /: $(df -Pm / | awk 'NR==2{print $4}') MiB"
}

trap 'rm -f "$REPORT"; echo; echo "Relevamiento interrumpido: no se guardó el reporte (estaba incompleto)." >&2; exit 130' INT TERM HUP
(
  system; network; firewall; panels; docker_engine; containers; projects; networks_volumes; web; container_proxies; tls
  host_services; layout; summary
  echo; echo "Fin del relevamiento. No se modificó nada."
) 2>&1 | redact | tee "$REPORT"
rc=${PIPESTATUS[0]}
echo
if [[ $rc -ne 0 ]]; then
  echo "El relevamiento NO terminó (código $rc): el reporte en $REPORT está incompleto."
else
  echo "Reporte guardado en $REPORT (solo root puede leerlo). Revisalo antes de compartirlo."
fi
