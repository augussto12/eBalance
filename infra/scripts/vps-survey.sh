#!/usr/bin/env bash
# Relevamiento de SOLO LECTURA de una VPS con otros proyectos: qué corre, cómo está conectado y qué recursos quedan.
# No instala, no reinicia, no modifica configuración ni contenedores. Lo único que escribe es el reporte (modo 600)
# en un archivo temporal. NO muestra variables de entorno, contenidos de .env/compose ni claves; además, todo pasa
# por un filtro que tapa contraseñas, tokens y credenciales en URLs.
#
#   curl -fsSL https://raw.githubusercontent.com/augussto12/eBalance/implementacion-v1/infra/scripts/vps-survey.sh -o vps-survey.sh
#   less vps-survey.sh            # revisalo
#   sudo bash vps-survey.sh       # imprime el reporte y lo guarda en /tmp/relevamiento-vps.XXXXXX.txt
set -uo pipefail
export LC_ALL=C

[[ $EUID -eq 0 ]] || { echo "Corré el relevamiento como root (sudo bash vps-survey.sh)." >&2; exit 1; }
umask 077
REPORT="$(mktemp /tmp/relevamiento-vps.XXXXXX.txt)"
T=20   # segundos máximos por comando

# Filtro de redacción: credenciales en URLs, pares clave=valor sensibles, JWT, claves privadas y cadenas largas aleatorias.
redact() {
  sed -E \
    -e 's#(://)[^/@[:space:]:]+:[^/@[:space:]]+@#\1***:***@#g' \
    -e 's#((pass(word|wd)?|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|client[_-]?secret|credentials?|bearer|authorization|basicauth[._a-z]*users|digestauth[._a-z]*users|auth[_-]?key|salt|cookie)["'"'"']?[[:space:]]*[:=][[:space:]]*["'"'"']?)[^"'"'"'[:space:],;]+#\1<redactado>#Ig' \
    -e 's#eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}#<redactado-jwt>#g' \
    -e 's#-----BEGIN [A-Z ]*PRIVATE KEY-----.*#<redactado-clave-privada>#g' \
    -e 's#[A-Za-z0-9+_-]{40,}={0,2}#<redactado-largo>#g'
}

section() { printf '\n==================== %s ====================\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }
# Ejecuta un comando con tiempo máximo; los errores se muestran pero no detienen el relevamiento.
run() { timeout "$T" "$@" 2>&1 || echo "(sin datos: «$*» terminó con código $?)"; }

main() {
  echo "Relevamiento de solo lectura · $(date -u +%FT%TZ) · $(hostname -f 2>/dev/null || hostname)"

  section "1. Sistema"
  . /etc/os-release 2>/dev/null; echo "SO: ${PRETTY_NAME:-?} · kernel $(uname -r) · $(uname -m) · virtualización: $(systemd-detect-virt 2>/dev/null || echo ?)"
  echo "CPU: $(nproc) · carga: $(cut -d' ' -f1-3 /proc/loadavg) · encendida: $(uptime -p 2>/dev/null)"
  run free -m
  awk '/^(MemTotal|MemAvailable|SwapTotal|SwapFree):/{printf "%s %d MiB\n", $1, $2/1024}' /proc/meminfo
  run df -hT -x tmpfs -x devtmpfs -x overlay -x squashfs
  echo "Zona horaria: $(timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null)"

  section "2. Red"
  run ip -br addr
  run ip -4 route
  echo "Rutas IPv6 globales:"; ip -6 route 2>/dev/null | grep -v '^fe80' | head -10
  echo "Resolvers: $(awk '/^nameserver/{print $2}' /etc/resolv.conf | tr '\n' ' ')"
  echo "IP pública (vista desde afuera): $(curl -4 -fsS --max-time 6 https://api.ipify.org 2>/dev/null || echo ?)"

  section "3. Puertos escuchando (TCP y UDP, con proceso)"
  run ss -Htlnp
  echo "--- UDP"; run ss -Hulnp
  echo "--- Cuáles están abiertos a todas las interfaces (0.0.0.0 / [::] / *):"
  ss -Htln 2>/dev/null | awk '{print $4}' | grep -E '^(0\.0\.0\.0|\*|\[::\]):' | sort -u | tr '\n' ' '; echo
  echo "--- Posibles bases de datos o paneles expuestos a internet:"
  ss -Htln 2>/dev/null | awk '{print $4}' | grep -E '^(0\.0\.0\.0|\*|\[::\]):(5432|3306|33060|6379|27017|9200|5984|11211|2375|2376|9000|9443|8000|10000|5601|15672)$' \
    || echo "(ninguno de los puertos típicos)"

  section "4. Firewall"
  if have ufw; then run ufw status verbose; else echo "ufw no instalado"; fi
  if have nft; then echo "--- nftables (tablas):"; nft list tables 2>/dev/null; fi
  echo "--- iptables INPUT:"; iptables -S INPUT 2>/dev/null | head -40
  echo "--- iptables DOCKER-USER:"; iptables -S DOCKER-USER 2>/dev/null | head -20
  if have fail2ban-client; then run fail2ban-client status; fi

  section "5. SSH (configuración efectiva, sin claves)"
  if have sshd; then sshd -T 2>/dev/null | grep -Ei '^(port|permitrootlogin|passwordauthentication|pubkeyauthentication|kbdinteractiveauthentication|allowusers|allowgroups) '; fi
  echo "Claves autorizadas de root: $(grep -cvE '^\s*(#|$)' /root/.ssh/authorized_keys 2>/dev/null || echo 0)"

  section "6. Paneles y orquestadores conocidos"
  for p in /data/coolify /etc/easypanel /etc/dokploy /usr/local/hestia /usr/local/cpanel /usr/local/CyberCP /www/server/panel /usr/local/lsws /opt/portainer /captain; do
    [[ -e "$p" ]] && echo "Encontrado: $p"
  done
  docker ps --format '{{.Names}} {{.Image}}' 2>/dev/null \
    | grep -Ei 'coolify|dokploy|easypanel|portainer|caprover|captain|traefik|nginx-proxy|proxy-manager|jc21|caddy|haproxy|npm|watchtower|cloudflared|tailscale' \
    || echo "(ningún contenedor de panel/proxy conocido por nombre o imagen)"

  section "7. Docker: motor"
  if ! have docker; then echo "Docker no instalado"; else
    run docker version --format 'Cliente {{.Client.Version}} · Servidor {{.Server.Version}} · API {{.Server.APIVersion}}'
    run docker compose version
    docker info --format 'Driver: {{.Driver}} · Raíz: {{.DockerRootDir}} · Logs: {{.LoggingDriver}} · cgroup: {{.CgroupDriver}} v{{.CgroupVersion}} · Contenedores: {{.Containers}} ({{.ContainersRunning}} corriendo) · Imágenes: {{.Images}} · Live restore: {{.LiveRestoreEnabled}} · Seguridad: {{.SecurityOptions}}' 2>&1 | head -3
    echo "--- /etc/docker/daemon.json:"; cat /etc/docker/daemon.json 2>/dev/null || echo "(no existe)"
    echo "--- Espacio:"; run docker system df
  fi
}

containers() {
  have docker || return 0
  section "8. Contenedores (sin variables de entorno)"
  local ids; ids="$(docker ps -aq 2>/dev/null)"
  [[ -n "$ids" ]] || { echo "(no hay contenedores)"; return 0; }
  local id
  for id in $ids; do
  # shellcheck disable=SC2016
  docker inspect -f '
--- {{.Name}}  [{{.State.Status}}{{with index .State "Health"}} · salud: {{.Status}}{{end}} · reinicios: {{.RestartCount}}]
    imagen: {{.Config.Image}}
    compose: proyecto={{index .Config.Labels "com.docker.compose.project"}} servicio={{index .Config.Labels "com.docker.compose.service"}} dir={{index .Config.Labels "com.docker.compose.project.working_dir"}}
    reinicio: {{.HostConfig.RestartPolicy.Name}} · red: {{.HostConfig.NetworkMode}} · privilegiado: {{.HostConfig.Privileged}} · usuario: "{{.Config.User}}" · memoria máx: {{.HostConfig.Memory}} · solo lectura: {{.HostConfig.ReadonlyRootfs}}
    puertos publicados: {{range $p, $b := .HostConfig.PortBindings}}{{range $b}}{{if .HostIp}}{{.HostIp}}{{else}}0.0.0.0{{end}}:{{.HostPort}}->{{$p}} {{end}}{{end}}
    redes: {{range $n, $c := .NetworkSettings.Networks}}{{$n}}({{$c.IPAddress}}) {{end}}
    montajes: {{range .Mounts}}{{.Type}}:{{if .Name}}{{.Name}}{{else}}{{.Source}}{{end}}->{{.Destination}}{{if not .RW}}(ro){{end}} {{end}}
    etiquetas de enrutamiento: {{range $k, $v := .Config.Labels}}{{if or (eq (printf "%.7s" $k) "traefik") (eq (printf "%.5s" $k) "caddy")}}{{$k}}={{$v}} {{end}}{{end}}' "$id" 2>&1
  done
  echo
  echo "--- Variables de entorno de ruteo (solo nombres de host/puertos de nginx-proxy/acme-companion):"
  for id in $ids; do
    docker inspect -f '{{.Name}} {{range .Config.Env}}{{.}} {{end}}' "$id" 2>/dev/null \
      | grep -oE '^/[^ ]+|(VIRTUAL_HOST|VIRTUAL_PORT|LETSENCRYPT_HOST)=[^ ]+' | paste -sd' ' | grep '=' || true
  done
  echo "--- docker.sock montado en:"; docker inspect -f '{{.Name}}{{range .Mounts}}{{if eq .Source "/var/run/docker.sock"}} ← docker.sock{{end}}{{end}}' $ids 2>/dev/null | grep 'docker.sock' || echo "(ninguno)"
  echo "--- Uso actual de recursos:"; run docker stats --no-stream --format 'table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.MemPerc}}'
}

projects() {
  have docker || return 0
  section "9. Proyectos de Docker Compose (archivos listados, contenidos NO)"
  local dirs
  dirs="$(docker ps -a --format '{{.Label "com.docker.compose.project.working_dir"}}' 2>/dev/null | sort -u | grep -v '^$')"
  [[ -n "$dirs" ]] || { echo "(ningún contenedor creado con Compose)"; return 0; }
  while IFS= read -r d; do
    echo "--- $d"
    echo "    proyecto: $(docker ps -a --filter "label=com.docker.compose.project.working_dir=$d" --format '{{.Label "com.docker.compose.project"}}' | sort -u | tr '\n' ' ')"
    echo "    archivos de compose: $(docker ps -a --filter "label=com.docker.compose.project.working_dir=$d" --format '{{.Label "com.docker.compose.project.config_files"}}' | sort -u | tr '\n' ' ')"
    if [[ -d "$d" ]]; then
      echo "    contenido del directorio (nombres):"; ls -1A "$d" 2>/dev/null | head -40 | sed 's/^/      /'
      [[ -f "$d/.env" ]] && echo "    tiene .env (no se muestra)"
      if [[ -d "$d/.git" ]] || git -C "$d" rev-parse --git-dir >/dev/null 2>&1; then
        echo "    git: $(git -C "$d" remote get-url origin 2>/dev/null) · rama $(git -C "$d" rev-parse --abbrev-ref HEAD 2>/dev/null) · $(git -C "$d" log -1 --format='%h %cs' 2>/dev/null)"
      fi
    else
      echo "    (el directorio ya no existe en el host)"
    fi
  done <<<"$dirs"
  echo "--- Contenedores sin Compose (creados con docker run):"
  docker ps -a --format '{{.Names}} {{.Label "com.docker.compose.project"}}' 2>/dev/null | awk 'NF==1{print "    " $1; n++} END{if(!n) print "    (ninguno)"}'
  echo "--- docker compose ls:"; run docker compose ls -a
}

networks_volumes() {
  have docker || return 0
  section "10. Redes de Docker"
  # shellcheck disable=SC2016
  docker network ls -q 2>/dev/null | xargs -r docker network inspect -f '{{.Name}} · {{.Driver}} · interna={{.Internal}} · subred={{range .IPAM.Config}}{{.Subnet}} {{end}}· contenedores: {{range .Containers}}{{.Name}} {{end}}' 2>&1
  section "11. Volúmenes e imágenes"
  run docker system df -v 2>&1 | awk '/^Local Volumes space usage/,/^Build cache usage/' | head -60
  echo "--- Imágenes (repositorio:tag, tamaño):"; docker image ls --format '{{.Repository}}:{{.Tag}}  {{.Size}}  {{.CreatedSince}}' 2>/dev/null | head -60
}

web() {
  section "12. Servidores web y proxys del HOST"
  for s in nginx apache2 httpd caddy haproxy traefik lighttpd openresty; do
    systemctl is-active --quiet "$s" 2>/dev/null && echo "Servicio activo: $s ($(systemctl is-enabled "$s" 2>/dev/null))"
  done
  if have nginx; then
    echo "nginx: $(nginx -v 2>&1 | cut -d/ -f2)"
    echo "--- Sitios habilitados:"; ls -la /etc/nginx/sites-enabled/ /etc/nginx/conf.d/ 2>/dev/null
    echo "--- nginx -t:"; run nginx -t
    echo "--- Bloques server (archivo, listen, server_name, destino, certificado):"
    timeout "$T" nginx -T 2>/dev/null | awk '
      /^# configuration file /{file=$4; sub(/:$/,"",file); next}
      {
        line=$0; sub(/#.*/,"",line)
        if (line ~ /^[[:space:]]*(server|upstream[[:space:]]+[^[:space:]{]+)[[:space:]]*\{/ && blk==0) {
          blk=depth+1; hdr=line; gsub(/^[[:space:]]+|[[:space:]]*\{.*$/,"",hdr)
          print "\n  [" file "] " hdr
        } else if (blk && line ~ /^[[:space:]]*(listen|server_name|proxy_pass|root|return|ssl_certificate|server|location|auth_basic)[[:space:]]/) {
          d=line; gsub(/^[[:space:]]+|[[:space:]]*\{[[:space:]]*$/,"",d); print "    " d
        }
        o=gsub(/\{/,"{",line); c=gsub(/\}/,"}",line); depth+=o-c
        if (blk && depth<blk) blk=0
      }' | head -300
  fi
  if have apache2ctl; then echo "--- Apache:"; run apache2ctl -S; fi
  echo "--- Qué responde en 80/443 de esta máquina:"
  for u in http://127.0.0.1/ https://127.0.0.1/; do
    echo "    $u → $(curl -sk -o /dev/null --max-time 5 -w '%{http_code} %{redirect_url}' "$u" 2>/dev/null) · $(curl -skI --max-time 5 "$u" 2>/dev/null | grep -i '^server:' | tr -d '\r')"
  done
  echo "--- Qué sirve cada puerto TCP abierto a todas las interfaces (código HTTP, título):"
  for port in $(ss -Htln 2>/dev/null | awk '{print $4}' | grep -E '^(0\.0\.0\.0|\*|\[::\]):' | sed -E 's/.*:([0-9]+)$/\1/' | sort -nu | head -40); do
    [[ "$port" == 22 ]] && continue
    out="$(curl -s --max-time 3 "http://127.0.0.1:$port/" 2>/dev/null | head -c 20000)"
    code="$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "http://127.0.0.1:$port/" 2>/dev/null)"
    title="$(grep -oiE '<title>[^<]{0,80}' <<<"$out" | head -1 | sed 's/<title>//I')"
    echo "    :$port → HTTP ${code:-?} ${title:+«$title»}"
  done
  echo "--- Proxys dentro de contenedores:"
  if have docker; then
    for c in $(docker ps --format '{{.Names}} {{.Image}}' 2>/dev/null | grep -Ei 'nginx|openresty|proxy-manager|jc21' | awk '{print $1}'); do
      echo "  [$c] (nginx -T resumido, solo lectura)"
      timeout "$T" docker exec "$c" nginx -T 2>/dev/null | awk '
        { line=$0; sub(/#.*/,"",line) }
        line ~ /^[[:space:]]*(listen|server_name|proxy_pass|ssl_certificate)[[:space:]]/ {gsub(/^[[:space:]]+/,"",line); print "    " line}' | sort | uniq -c | sort -rn | head -60
    done
    for c in $(docker ps --format '{{.Names}} {{.Image}}' 2>/dev/null | grep -Ei 'traefik' | awk '{print $1}'); do
      echo "  [$c] argumentos de Traefik:"; docker inspect -f '{{range .Config.Cmd}}{{.}}{{"\n"}}{{end}}{{range .Args}}{{.}}{{"\n"}}{{end}}' "$c" 2>/dev/null | sed 's/^/    /'
    done
    for c in $(docker ps --format '{{.Names}} {{.Image}}' 2>/dev/null | grep -Ei 'caddy' | awk '{print $1}'); do
      echo "  [$c] Caddyfile (sitios):"; timeout "$T" docker exec "$c" sh -c 'cat /etc/caddy/Caddyfile 2>/dev/null' | grep -E '^[^[:space:]#].*\{' | sed 's/^/    /'
    done
  fi
}

tls() {
  section "13. Certificados TLS en el host"
  if have certbot; then run certbot certificates; fi
  for d in /etc/letsencrypt/live/*/ /etc/alvarez-finanzas/letsencrypt/live/*/; do
    [[ -f "$d/cert.pem" ]] || continue
    echo "  $d: $(openssl x509 -in "$d/cert.pem" -noout -enddate -ext subjectAltName 2>/dev/null | tr '\n' ' ')"
  done
  echo "--- Renovación (authenticator por certificado):"; grep -H '^authenticator' /etc/letsencrypt/renewal/*.conf 2>/dev/null || echo "(sin certbot del sistema)"
  echo "--- Otros clientes ACME:"; for p in /root/.acme.sh /etc/caddy /var/lib/caddy; do [[ -e "$p" ]] && echo "  $p"; done
  have docker && docker ps --format '{{.Names}} {{.Mounts}}' 2>/dev/null | grep -iE 'acme|letsencrypt|certs' | sed 's/^/  /'
}

host_services() {
  section "14. Servicios, timers y tareas programadas"
  echo "--- Servicios en ejecución (excepto los de base del sistema):"
  systemctl list-units --type=service --state=running --no-legend --plain 2>/dev/null | awk '{print $1}' \
    | grep -vE '^(systemd-|dbus|cron|rsyslog|ssh|getty|serial-getty|polkit|networkd-dispatcher|unattended-upgrades|udisks2|ModemManager|multipathd|snapd|qemu-guest-agent|user@|accounts-daemon|irqbalance|packagekit|containerd|docker)' | sed 's/^/  /'
  echo "--- Unidades fallidas:"; systemctl --failed --no-legend --plain 2>/dev/null | sed 's/^/  /'; echo
  echo "--- Timers:"; systemctl list-timers --all --no-legend --plain 2>/dev/null | awk '{print "  " $(NF-1), $NF}' | head -30
  echo "--- cron (horario y comando, filtrado):"
  { cat /etc/crontab /etc/cron.d/* 2>/dev/null; crontab -l 2>/dev/null; for u in /var/spool/cron/crontabs/*; do [[ -f "$u" ]] && sed "s#^#[$(basename "$u")] #" "$u"; done; } \
    | grep -vE '^\s*(#|$|SHELL=|PATH=|MAILTO=)' | sort -u | head -60
  echo "--- Procesos que más memoria usan:"; ps -eo rss,comm --sort=-rss 2>/dev/null | head -15 | awk 'NR==1{print "  MiB  proceso"; next}{printf "  %4d %s\n", $1/1024, $2}'
}

layout() {
  section "15. Dónde viven los proyectos (tamaños de primer nivel)"
  for d in /srv /opt /var/www /home /root /data /apps /docker; do
    [[ -d "$d" ]] || continue
    echo "--- $d"; timeout "$T" du -xsh "$d"/* "$d"/.[!.]* 2>/dev/null | sort -rh | head -20 | sed 's/^/  /'
  done
}

summary() {
  section "16. Resumen para la instalación de Álvarez Finanzas"
  echo "Puerto 80: $(ss -Htlnp 'sport = :80' 2>/dev/null | grep -o 'users:(("[^"]*"' | head -1 | cut -d'"' -f2 || true)"
  echo "Puerto 443: $(ss -Htlnp 'sport = :443' 2>/dev/null | grep -o 'users:(("[^"]*"' | head -1 | cut -d'"' -f2 || true)"
  if have docker; then
    echo "Contenedores que publican 80/443: $(docker ps --format '{{.Names}} {{.Ports}}' 2>/dev/null | grep -E '(^|[ ,:])(80|443)->' | awk '{print $1}' | tr '\n' ' ')"
    echo "Subredes de Docker en uso: $(docker network ls -q 2>/dev/null | xargs -r docker network inspect -f '{{range .IPAM.Config}}{{.Subnet}} {{end}}' 2>/dev/null | tr -s ' \n' ' ')"
  fi
  echo "¿172.30.57.0/24 libre? $(ip -4 route 2>/dev/null | grep -q '172\.30\.57\.' && echo NO || echo sí) · ¿127.0.0.1:8088 libre? $(ss -Htln 'sport = :8088' 2>/dev/null | grep -q . && echo NO || echo sí)"
  echo "Memoria disponible: $(awk '/^MemAvailable:/{a=$2} /^SwapFree:/{s=$2} END{printf "%d MiB (RAM+swap libres)", (a+s)/1024}' /proc/meminfo) · Disco libre en /: $(df -Pm / | awk 'NR==2{print $4}') MiB"
}

{
  main; containers; projects; networks_volumes; web; tls; host_services; layout; summary
  echo; echo "Fin del relevamiento. No se modificó nada."
} 2>&1 | redact | tee "$REPORT"
echo
echo "Reporte guardado en $REPORT (solo root puede leerlo). Revisalo antes de compartirlo."
