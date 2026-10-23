#!/usr/bin/env bash
# Extrae la configuración no secreta respaldada (etiqueta «config»: .env, sitio de Nginx, unidades systemd, commit)
# en un directorio NUEVO para revisarla. No instala nada: copiá a mano lo que corresponda (RUNBOOK §5).
#   sudo ./infra/scripts/restore-config.sh [snapshot|latest] [DESTINO]
source "$(dirname "$0")/lib.sh"
load_env
snap="${1:-latest}"
dest="${2:-$(mktemp -d /tmp/alvarez-config.XXXXXX)}"
[[ ! -e "$dest" || -z "$(ls -A "$dest")" ]] || die "$dest no está vacío."
mkdir -p "$dest"; chmod 700 "$dest"
# Como root (--user 0): escribe en un directorio del host y conserva dueños y permisos de los archivos.
backup_ops --user 0 -v "$(realpath "$dest"):/out" -- restore-config "$snap" /out
info "Configuración extraída en $dest/config (revisala antes de copiar nada al sistema)."
