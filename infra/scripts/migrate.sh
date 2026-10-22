#!/usr/bin/env bash
# Aplica migraciones UNA vez con el rol migrador (DDL). La API corre con el rol runtime (sin DDL).
# Nunca EnsureCreated ni drop/recreate.
source "$(dirname "$0")/lib.sh"
load_env
dc up -d db
dc --profile ops run --rm migrate
