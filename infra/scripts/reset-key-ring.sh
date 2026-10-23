#!/usr/bin/env bash
# Acepta la pérdida DEFINITIVA del key ring de Data Protection, cuando no hay copia para restaurar. En ese estado la API se
# niega a arrancar («Key ring rechazado» en el log): no genera claves nuevas en silencio. Este comando, con confirmación
# escrita, reinicia el segundo factor de los usuarios cuyo secreto ya no se puede descifrar, revoca todas las sesiones y
# registra un centinela nuevo con las claves actuales. No toca datos financieros. Queda auditado (cli.reset_key_ring).
# Si el key ring está en un backup, NO uses esto: ./infra/scripts/restore.sh --keyring-only (no toca la base)
#   ./infra/scripts/reset-key-ring.sh
source "$(dirname "$0")/lib.sh"
load_env
warn "Solo si el key ring se perdió sin copia. Con backup: ./infra/scripts/restore.sh --keyring-only (restaura solo las claves; no toca la base)"
dc up -d db
# La API no arranca en este estado: se detiene y el comando corre en un contenedor de un solo uso con su configuración y volúmenes.
dc stop web api
if [[ -t 0 ]]; then
  dc run --rm --no-deps api reset-key-ring
else
  dc run --rm --no-deps -T api reset-key-ring
fi
dc up -d api web
info "Iniciá sesión y configurá el segundo factor de nuevo; después generá códigos de recuperación nuevos."
