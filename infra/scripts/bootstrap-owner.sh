#!/usr/bin/env bash
# Crea el ÚNICO propietario desde una consola confiable. La contraseña se escribe de forma oculta (TTY) o por stdin;
# nunca como argumento, variable de entorno ni archivo de Compose.
#   ./infra/scripts/bootstrap-owner.sh --username augusto --display-name "Augusto Álvarez"
source "$(dirname "$0")/lib.sh"
load_env
if [[ -t 0 ]]; then
  dc exec api dotnet AlvarezFinanzas.Api.dll bootstrap-owner "$@"
else
  dc exec -T api dotnet AlvarezFinanzas.Api.dll bootstrap-owner "$@"
fi
