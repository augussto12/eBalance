#!/usr/bin/env bash
# Funciones comunes. Los scripts se ejecutan desde cualquier lugar; ROOT es la raíz del repo.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

die() { echo "ERROR: $*" >&2; exit 1; }
info() { echo "==> $*"; }
warn() { echo "AVISO: $*" >&2; }

load_env() {
  [[ -f .env ]] || die "Falta .env (copiá .env.example y completalo)."
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
  SECRETS_DIR="${SECRETS_DIR:-./secrets}"
  BACKUP_DIR="${BACKUP_DIR:-./backups}"
  # Credenciales del repositorio externo (variables de restic, NOMBRE=valor). Ver RUNBOOK §4.
  OFFSITE_RESTIC_ENV_FILE="${OFFSITE_RESTIC_ENV_FILE:-$SECRETS_DIR/restic-offsite.env}"
}

# COMPOSE_FILE (en .env) permite agregar overlays, p. ej. el entorno E2E local.
dc() {
  if [[ -n "${COMPOSE_FILE:-}" ]]; then docker compose --env-file .env "$@"
  else docker compose --env-file .env -f compose.yaml "$@"; fi
}

# Confirmación explícita escribiendo una palabra (no acepta «y» por defecto).
confirm_word() {
  local word="$1" prompt="$2" answer
  # Desde la terminal si existe (los comandos de Docker pueden consumir stdin).
  if [[ -r /dev/tty ]] && { : < /dev/tty; } 2>/dev/null; then
    read -r -p "$prompt Escribí «$word» para continuar: " answer < /dev/tty
  else
    read -r -p "$prompt Escribí «$word» para continuar: " answer
  fi
  [[ "$answer" == "$word" ]] || die "Cancelado."
}

# UIDs de las imágenes: postgres (alpine) = 70; .NET aspnet «app» = 1654. Los usan los scripts que cargan lib.sh.
# shellcheck disable=SC2034
PG_UID=70
APP_UID=1654
# El contenedor de backup corre como 1654 (compose.yaml, «user»): es el dueño del key ring, que lee sin privilegios.
BACKUP_UID=$APP_UID

wait_healthy() {
  local svc="$1" id status
  for _ in $(seq 1 45); do
    id="$(dc ps -q "$svc")"
    status="$(docker inspect -f '{{.State.Health.Status}}' "$id" 2>/dev/null || echo starting)"
    [[ "$status" == healthy ]] && { info "$svc healthy"; return 0; }
    sleep 2
  done
  dc logs --tail 50 "$svc" >&2
  die "$svc no quedó healthy"
}

# Permisos que necesita el contenedor de backup sin root: BACKUP_DIR atravesable por su grupo, el repositorio local
# suyo y la contraseña de restic (y las credenciales externas) legibles por su grupo. Idempotente; requiere root.
prepare_backup_perms() {
  local repo="${RESTIC_REPOSITORY:-/backups/restic}" host_repo="" f
  [[ "$repo" == /backups/* ]] && host_repo="$BACKUP_DIR/${repo#/backups/}"
  mkdir -p "$BACKUP_DIR"
  if [[ $EUID -ne 0 ]]; then
    warn "Sin root no se ajustan los permisos de $BACKUP_DIR ni los de la contraseña de restic (corré con sudo)."
    return 0
  fi
  chown "0:$BACKUP_UID" "$BACKUP_DIR"; chmod 750 "$BACKUP_DIR"
  if [[ -n "$host_repo" ]]; then
    mkdir -p "$host_repo"
    find "$host_repo" \( ! -user "$BACKUP_UID" -o ! -group "$BACKUP_UID" \) -exec chown -h "$BACKUP_UID:$BACKUP_UID" {} +
    chmod 700 "$host_repo"
  fi
  for f in "$SECRETS_DIR/restic_password" "$SECRETS_DIR/restic_password.previous" "$OFFSITE_RESTIC_ENV_FILE"; do
    [[ -f "$f" ]] || continue
    chown "0:$BACKUP_UID" "$f"; chmod 440 "$f"
  done
}

offsite_configured() { [[ -n "${OFFSITE_RESTIC_REPOSITORY:-}" ]]; }

# Ejecuta infra/docker/backup-ops.sh dentro del contenedor de backup. Se monta desde el checkout (no está en la imagen):
# funciona igual con la imagen de un release anterior después de un rollback. Las opciones para «docker compose run»
# van antes de «--», p. ej.: backup_ops -v "$archivo:/run/alvarez/new_password:ro" -- key-add local /run/alvarez/new_password
backup_ops() {
  local args=(-v "$ROOT/infra/docker/backup-ops.sh:/opt/alvarez/backup-ops.sh:ro")
  if [[ " $* " == *" -- "* ]]; then
    while [[ "$1" != -- ]]; do args+=("$1"); shift; done
    shift
  fi
  if offsite_configured; then
    args+=(-e "OFFSITE_RESTIC_REPOSITORY=$OFFSITE_RESTIC_REPOSITORY" -e "OFFSITE_PRUNE=${OFFSITE_PRUNE:-1}")
    # Las credenciales se leen DENTRO del contenedor desde un archivo de solo lectura (compose run no tiene --env-file).
    [[ -f "$OFFSITE_RESTIC_ENV_FILE" ]] && args+=(-v "$(realpath "$OFFSITE_RESTIC_ENV_FILE"):/run/alvarez/offsite.env:ro")
  fi
  dc --profile ops run --rm -T "${args[@]}" --entrypoint sh backup /opt/alvarez/backup-ops.sh "$@" </dev/null
}

# Espacio libre (mínimo 2 GiB) en cada sistema de archivos que usa la app: el repo, los datos de Docker (imágenes,
# volúmenes de la base y del key ring) y BACKUP_DIR. Llama a «$1 mensaje» si alcanza y a «$2 mensaje» si no.
check_disks() {
  local okf="$1" badf="$2" rc=0 entry label path avail mnt docker_root i found
  local mounts=() labels=() avails=()
  docker_root="$(docker info -f '{{.DockerRootDir}}' 2>/dev/null || true)"
  for entry in "repo:$ROOT" "docker:$docker_root" "backups:$BACKUP_DIR"; do
    label="${entry%%:*}"; path="${entry#*:}"
    if [[ -z "$path" ]]; then "$badf" "no se pudo saber dónde guarda Docker sus datos (docker info)"; rc=1; continue; fi
    while [[ ! -e "$path" && "$path" != / && "$path" != . ]]; do path="$(dirname "$path")"; done
    read -r avail mnt < <(df -Pk "$path" | awk 'NR==2{print $4, $6}')
    found=-1
    for i in "${!mounts[@]}"; do [[ "${mounts[$i]}" == "$mnt" ]] && found=$i; done
    if (( found < 0 )); then mounts+=("$mnt"); labels+=("$label"); avails+=("$avail")
    else labels[found]+=", $label"; fi
  done
  for i in "${!mounts[@]}"; do
    avail="${avails[$i]}"
    if (( avail > 2*1024*1024 )); then "$okf" "espacio libre en ${mounts[$i]} (${labels[$i]}): $(( avail / 1024 )) MiB"
    else "$badf" "menos de 2 GiB libres en ${mounts[$i]} (${labels[$i]}): $(( avail / 1024 )) MiB"; rc=1; fi
  done
  return $rc
}

# Reloj sincronizado (los códigos TOTP dependen de la hora). 0: sincronizado; 1: NO sincronizado según systemd;
# 2: no se puede confirmar (sin systemd solo se ve el estado del kernel, y una VM puede tomar la hora del hipervisor).
check_ntp() {
  local v
  if v="$(timedatectl show -p NTPSynchronized --value 2>/dev/null)" && [[ -n "$v" ]]; then
    if [[ "$v" == yes ]]; then echo "reloj sincronizado por NTP"; return 0; fi
    echo "el reloj NO está sincronizado por NTP (TOTP depende de la hora): «sudo timedatectl set-ntp true» y revisá «timedatectl timesync-status»"
    return 1
  fi
  # adjtimex en modo lectura: el mismo dato que informa timedatectl (5 = TIME_ERROR: reloj no sincronizado).
  v="$(python3 -c 'import ctypes,ctypes.util; b=ctypes.create_string_buffer(512); print(ctypes.CDLL(ctypes.util.find_library("c")).adjtimex(b))' 2>/dev/null || true)"
  case "$v" in
    0|1|2|3|4) echo "reloj sincronizado según el kernel (sin timedatectl)"; return 0 ;;
    5) echo "sin timedatectl, y el kernel indica que ningún servicio NTP ajusta el reloj: verificá la hora (TOTP depende de ella)"; return 2 ;;
    *) echo "no se pudo verificar la sincronización del reloj (sin timedatectl)"; return 2 ;;
  esac
}

# Verifica el certificado TLS público: cadena de confianza, que cubra el nombre/IP de PUBLIC_ORIGIN y vencimiento.
# Umbral proporcional a su vida útil: los de IP duran ~6 días y certbot los renueva a mitad de vida (alerta con
# < 2 días restantes); los normales, 90 (alerta con < 14). TLS_CA_FILE: CA propia (solo pruebas). Devuelve 0/1.
check_tls() {
  local hostport="${PUBLIC_ORIGIN#https://}" host out cert code start end now left life min
  host="${hostport%%:*}"; [[ "$hostport" == *:* ]] || hostport="$hostport:443"
  local sni=(-servername "$host") verify=(-verify_hostname "$host") ca=()
  if [[ "$host" =~ ^[0-9.]+$ ]]; then sni=(); verify=(-verify_ip "$host"); fi
  [[ -n "${TLS_CA_FILE:-}" ]] && ca=(-CAfile "$TLS_CA_FILE")
  out="$(echo | timeout 10 openssl s_client "${sni[@]}" "${verify[@]}" "${ca[@]}" -connect "$hostport" 2>&1 || true)"
  cert="$(openssl x509 -noout -startdate -enddate 2>/dev/null <<<"$out")" \
    || { echo "No se pudo leer el certificado TLS de $hostport"; return 1; }
  code="$(sed -n 's/^ *Verify return code: \([0-9]*\).*/\1/p' <<<"$out" | tail -1)"
  if [[ "$code" != 0 ]]; then
    echo "El certificado TLS de $hostport no es válido para $host ($(grep -m1 -o 'Verify return code: .*' <<<"$out" || echo 'sin verificación'))"
    return 1
  fi
  start=$(date -d "$(sed -n 's/^notBefore=//p' <<<"$cert")" +%s); end=$(date -d "$(sed -n 's/^notAfter=//p' <<<"$cert")" +%s); now=$(date +%s)
  left=$(( (end - now) / 3600 )); life=$(( (end - start) / 3600 ))
  min=$(( life <= 240 ? 48 : 14 * 24 ))
  if (( left < min )); then echo "El certificado TLS vence en ${left} h (vida ${life} h): revisá la renovación (certbot renew)"; return 1; fi
  echo "TLS válido: ${left} h restantes de ${life} h"
}
