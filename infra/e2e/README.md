# Entorno E2E local

Levanta el stack de producción (`compose.yaml`) más un Nginx «edge» con TLS autofirmado que usa la misma plantilla `infra/nginx/finanzas.conf` que el host. Solo para pruebas locales: **`run.sh` borra los volúmenes del proyecto local**.

`.env` de ejemplo (en la raíz, fuera de Git):

```
COMPOSE_PROJECT_NAME=alvarez-finanzas-e2e
COMPOSE_FILE=compose.yaml:infra/e2e/compose.e2e.yaml
PUBLIC_ORIGIN=https://localhost:8443
IMAGE_TAG=local
WEB_BIND=127.0.0.1:8088
SECRETS_DIR=/ruta/temporal/secrets
BACKUP_DIR=/ruta/temporal/backups
RESTIC_REPOSITORY=/backups/restic
```

```bash
./infra/e2e/build-images.sh            # EXTRA_CA_FILE=… solo detrás de un proxy TLS corporativo
sudo ./infra/scripts/bootstrap-secrets.sh
./infra/e2e/run.sh                     # migra, crea propietario ficticio y corre Playwright (tests/e2e)
```

Variables: `CHROMIUM_PATH` (usar un Chromium ya instalado), `E2E_USER`, `E2E_PASSWORD`. Las capturas y la comparación con `reference/` se guardan en `docs/visual/`.
