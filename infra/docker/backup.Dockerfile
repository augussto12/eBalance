# syntax=docker/dockerfile:1.7
# Backups: pg_dump 18 + restic (binario estático oficial). Cifrado del lado del cliente antes de salir del host.
ARG RESTIC_IMAGE=restic/restic:0.19.1
ARG POSTGRES_IMAGE=postgres:18.6-alpine3.24
FROM ${RESTIC_IMAGE} AS restic
FROM ${POSTGRES_IMAGE}
COPY --from=restic --chown=0:0 /usr/bin/restic /usr/local/bin/restic
COPY infra/docker/backup-entrypoint.sh /usr/local/bin/alvarez-backup
# gosu solo sirve para arrancar el servidor PostgreSQL, que esta imagen nunca ejecuta (y arrastra CVE de Go).
# Usuario 1654 (mismo uid que el key ring de la API): lee /keys sin DAC_OVERRIDE; restic y pg_dump no necesitan root.
RUN chmod 0755 /usr/local/bin/alvarez-backup \
    && rm -f /usr/local/bin/gosu \
    && addgroup -S -g 1654 alvarez-backup \
    && adduser -S -D -H -h /tmp -s /sbin/nologin -u 1654 -G alvarez-backup alvarez-backup
USER 1654:1654
ENTRYPOINT ["/usr/local/bin/alvarez-backup"]
