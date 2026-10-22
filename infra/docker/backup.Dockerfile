# syntax=docker/dockerfile:1.7
# Backups: pg_dump 18 + restic (binario estático oficial). Cifrado del lado del cliente antes de salir del host.
ARG RESTIC_IMAGE=restic/restic:0.19.1
ARG POSTGRES_IMAGE=postgres:18.6-alpine3.24
FROM ${RESTIC_IMAGE} AS restic
FROM ${POSTGRES_IMAGE}
COPY --from=restic /usr/bin/restic /usr/local/bin/restic
COPY infra/docker/backup-entrypoint.sh /usr/local/bin/alvarez-backup
RUN chmod 0755 /usr/local/bin/alvarez-backup
ENTRYPOINT ["/usr/local/bin/alvarez-backup"]
