#!/usr/bin/env bash
# Recuperación operativa por SSH autorizado: nueva contraseña, segundo factor reiniciado y todas las sesiones revocadas.
# Queda auditado (cli.recover_owner). No existe endpoint público equivalente.
#   ./infra/scripts/recover-owner.sh --username augusto
source "$(dirname "$0")/lib.sh"
load_env
if [[ -t 0 ]]; then
  dc exec api dotnet AlvarezFinanzas.Api.dll recover-owner "$@"
else
  dc exec -T api dotnet AlvarezFinanzas.Api.dll recover-owner "$@"
fi
