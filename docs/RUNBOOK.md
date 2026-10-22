# Runbook — VPS, despliegue, backups y recuperación

Todos los comandos se probaron en un entorno local equivalente (Docker Compose + Nginx con TLS), salvo los marcados **[no probado en VPS]**. En la VPS real todavía no se ejecutó nada: ver `IMPLEMENTATION-STATUS.md`.

## 0. Requisitos de la VPS

- Linux con systemd, Docker Engine y Docker Compose v2 (`docker compose version`), git, openssl, curl, python3.
- Nginx del host ya instalado (puede tener otros sitios: no se toca `nginx.conf`), certbot.
- SSH solo con claves; usuario administrativo con sudo. Firewall: solo 22 (idealmente restringido), 80 y 443.
- Pertenecer al grupo `docker` equivale a tener root: limitarlo al usuario administrativo.
- IPv6: la plantilla de Nginx escucha también en `[::]`. Si la VPS no tiene IPv6, borrá esas líneas (`nginx -t` lo detecta).

## 1. Primer despliegue

```bash
sudo mkdir -p /srv/alvarez-finanzas && sudo chown "$USER" /srv/alvarez-finanzas
git clone https://github.com/augussto12/eBalance.git /srv/alvarez-finanzas/app
cd /srv/alvarez-finanzas/app
cp .env.example .env && nano .env      # PUBLIC_ORIGIN=https://finanzas.tu-dominio, SECRETS_DIR, BACKUP_DIR
sudo ./infra/scripts/bootstrap-secrets.sh
```

`bootstrap-secrets.sh` genera contraseñas aleatorias de base y restic, y el certificado que cifra el key ring de Data Protection. Ajusta dueños y permisos: postgres uid 70, API uid 1654, modos 400/440 y directorio 700. **Nunca sobrescribe secretos existentes.** Guardá en tu gestor de contraseñas, **fuera del servidor**:

- `secrets/restic_password`: sin él, los backups son ilegibles.
- `secrets/keyring/current.pfx` y `current.password`: sin ellos no se pueden descifrar los segundos factores de un backup restaurado.

DNS: registro A (y AAAA si corresponde) de `finanzas.tu-dominio` apuntando a la VPS. Luego:

```bash
sudo certbot certonly --nginx -d finanzas.tu-dominio        # solo emite el certificado; no edita sitios
sudo ./infra/scripts/install-nginx-site.sh finanzas.tu-dominio   # valida con nginx -t antes de recargar
./infra/scripts/deploy.sh --first      # build con tag = commit, migración con rol migrador, arranque, smoke tests
./infra/scripts/bootstrap-owner.sh --username augusto --display-name "Augusto Álvarez"   # contraseña oculta por TTY
```

Abrí `https://finanzas.tu-dominio`, ingresá y configurá el autenticador (QR generado localmente). Guardá los 10 códigos de recuperación. Después:

```bash
./infra/scripts/preflight.sh           # debe terminar en «Preflight OK» (incluye TLS y check-config)
./infra/scripts/backup.sh
./infra/scripts/restore-test.sh        # debe terminar en resultado=OK ANTES de cargar datos reales
```

**HSTS:** cuando HTTPS funcione bien, descomentá `Strict-Transport-Security` en `/etc/nginx/sites-available/alvarez-finanzas.conf` y corré `sudo nginx -t && sudo systemctl reload nginx`. No agregues `includeSubDomains` ni `preload` sin revisar todos los subdominios.

**Comprobación externa de puertos [no probado en VPS]:** desde otra máquina, `nmap -Pn -p 1-65535 finanzas.tu-dominio` debe mostrar solo 22/80/443. En la VPS, `ss -tlnp` debe mostrar el contenedor web solo en `127.0.0.1:8088`, y ningún 5432 ni 8080.

## 2. Migrar los datos del prototipo

En el sitio anterior: «↓ Exportar respaldo» (JSON). En la app nueva: Configuración → Respaldo → «↑ Importar respaldo». Revisá la vista previa: cuentas nuevas con su tipo (marcá las tarjetas como «Tarjeta de crédito»), conflictos y advertencias. Recién ahí confirmá. Repetir la importación no duplica. **Las cuotas pasadas se importan como pendientes**: el archivo original no sabe qué pagaste; registrá los pagos o editá el plan como «crédito en curso». Al terminar, borrá las copias del JSON (están en texto claro).

## 3. Operación diaria

| Tarea | Comando |
|---|---|
| Actualizar a un commit nuevo | `git pull && ./infra/scripts/deploy.sh` (backup → migración → arranque → smoke) |
| Volver a la versión anterior | `./infra/scripts/deploy.sh --rollback <tag>` (no revierte migraciones; ver 6) |
| Estado / logs | `docker compose ps` · `docker compose logs --tail 100 api` |
| Verificación completa | `./infra/scripts/preflight.sh` |
| Backup manual | `./infra/scripts/backup.sh` |
| Prueba de restauración | `./infra/scripts/restore-test.sh [snapshot]` (mensual y antes de cambios grandes) |

**Programar backup y mantenimiento** (ajustá `WorkingDirectory` si el repo está en otra ruta):

```bash
sudo cp infra/systemd/alvarez-* /etc/systemd/system/ && sudo systemctl daemon-reload
sudo systemctl enable --now alvarez-backup.timer alvarez-maintenance.timer
systemctl list-timers 'alvarez-*'
```

`maintenance.sh` purga la auditoría de más de 90 días (con el rol migrador, porque el runtime no puede borrarla) y falla si: el disco tiene menos de 2 GiB libres, algún servicio no está healthy, el certificado vence en menos de 14 días o no hubo un backup exitoso en las últimas 48 h. **Alertas:** una unidad fallida queda en `systemctl --failed` y `journalctl -u alvarez-maintenance`. Para recibir avisos, configurá `OnFailure=` hacia un destino que elijas (correo local, ntfy propio, etc.). Los mensajes no deben llevar contenido financiero. **[no configurado: decisión del propietario]**

**Logs:** cada contenedor usa el driver `local` con rotación por tamaño (10 MB × 5). Eso limita el espacio, **no la antigüedad**. Para una retención estricta de 30 días, usá `logging.driver: journald` y en `/etc/systemd/journald.conf` poné `MaxRetentionSec=30day`. La API no registra cuerpos, cookies, tokens ni parámetros de consulta; Nginx registra rutas sin query string. Auditoría: tabla `audit_events`, 90 días.

## 4. Backups

- `backup.sh` genera un `pg_dump` consistente (rol migrador) más el key ring, cifrados por restic **antes** de escribir en `BACKUP_DIR`. Retención: últimas 10 + 7 diarios + 4 semanales + 3 mensuales. Al final ejecuta `restic check` sobre una muestra.
- **Copia fuera de la VPS (obligatoria para considerar el backup completo) [no probado]:** en `.env` definí `OFFSITE_RESTIC_REPOSITORY`, por ejemplo `sftp:usuario@otro-servidor:/backups/alvarez` o `rest:https://…`. Las credenciales del destino van en `SECRETS_DIR/restic-offsite.env` (variables de restic, modo 400). `backup.sh` copia las instantáneas con `restic copy`.
- El backup **no** incluye el certificado del key ring: está en tu gestor de contraseñas. No guardes la única clave junto al único backup.

## 5. Restauración

**Prueba aislada (no toca producción):** `./infra/scripts/restore-test.sh`. Levanta una base y una API en una red interna sin salida y restaura el dump y el key ring. Verifica conteos, sumas por moneda, consistencia de cuotas, descifrado de los secretos TOTP, que la API arranque y que Data Protection funcione. Revoca las sesiones restauradas y compara con producción. El resultado (fecha, duración, OK/FALLA) queda en `BACKUP_DIR/restore-tests.log`. El script no prueba un ingreso interactivo; hacelo a mano una vez por mes:

1. Restaurá en una máquina de prueba con `restore.sh`.
2. Ingresá con tu contraseña y tu autenticador.
3. Revisá Resumen y Movimientos.

**Restauración real:** `./infra/scripts/restore.sh <snapshot|latest> [--with-keyring]`.

- Pide escribir `RESTAURAR` (y `CLAVES` si restaurás el key ring) y fija el ID de la instantánea **antes** de hacer un backup de seguridad del estado actual (sin poda).
- Detiene la API y la web, restaura con `pg_restore` dejando las tablas a nombre del rol migrador, re-aplica permisos, revoca todas las sesiones y vuelve a levantar todo.
- Usá `--with-keyring` solo si se perdió el volumen de claves.
- Nunca se ejecuta automáticamente.

RPO objetivo ≤ 24 h (backup diario). RTO objetivo ≤ 4 h: en el entorno local, restaurar llevó menos de un minuto con pocos datos. Medilo en la VPS.

## 6. Migraciones y rollback

Las migraciones se aplican una sola vez por despliegue (`migrate`, rol migrador con DDL). La API usa el rol runtime: solo DML, sin UPDATE/DELETE sobre la auditoría y sin DDL. Para migraciones futuras, usar expand/contract: agregar sin romper la versión anterior y quitar en un despliegue posterior. Si una migración no es compatible hacia atrás, el rollback es `restore.sh` del backup previo al deploy (con pérdida de lo cargado después), dentro de una ventana de mantenimiento.

## 7. Rotación de secretos

```bash
./infra/scripts/rotate-secrets.sh db-app          # nueva contraseña del rol runtime + reinicio de la API (probado)
./infra/scripts/rotate-secrets.sh db-migrator     # nueva contraseña del rol migrador
./infra/scripts/rotate-secrets.sh keyring-cert    # certificado nuevo; el anterior queda como previous-*.pfx (probado)
./infra/scripts/rotate-secrets.sh restic          # agrega una clave nueva al repositorio y quita la anterior [no probado]
```

No borres `previous-*.pfx` mientras existan claves o backups que lo necesiten (se recomienda al menos un año). Actualizá tu gestor de contraseñas después de cada rotación.

## 8. Recuperación de acceso

- **Perdiste el celular, tenés códigos de recuperación:** en la pantalla del código, «Usar un código de recuperación». Esto cierra las demás sesiones. Después: Configuración → Seguridad → «Reconfigurar autenticador» y «Generar códigos nuevos».
- **Perdiste todo:** por SSH, `./infra/scripts/recover-owner.sh --username augusto`. Pide confirmar escribiendo el usuario, define una contraseña nueva (oculta), reinicia el segundo factor y revoca todas las sesiones. Queda auditado como `cli.recover_owner`. No existe recuperación pública por web.

## 9. Incidente (sospecha de acceso indebido)

1. **Restringir:** `docker compose stop web`, o bloquear el sitio en Nginx.
2. **Preservar:** copiá `docker compose logs api web` y los logs de Nginx. Exportá `audit_events` (`docker compose exec db psql -U alvarez_admin -d alvarez -c "\copy audit_events to '/tmp/audit.csv' csv header"`).
3. **Revocar sesiones:** `docker compose exec api dotnet AlvarezFinanzas.Api.dll revoke-sessions`.
4. **Rotar:** contraseña del propietario (`recover-owner.sh`), `rotate-secrets.sh db-app db-migrator keyring-cert restic` y las claves SSH de la VPS.
5. **Evaluar exposición:** revisá la auditoría (logins, exportaciones, importaciones) y los accesos SSH (`journalctl -u ssh`). Un root comprometido puede haber leído memoria, secretos y datos.
6. **Recuperar** desde una copia conocida y buena (`restore.sh`), en una VPS limpia si hubo compromiso del host.
7. **Revisar la causa** y documentarla.
