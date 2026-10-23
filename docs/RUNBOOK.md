# Runbook — VPS, despliegue, backups y recuperación

Todos los comandos se probaron en un entorno local equivalente (Docker Compose + Nginx con TLS), salvo los marcados **[no probado en VPS]**. En la VPS real todavía no se ejecutó nada: ver `IMPLEMENTATION-STATUS.md`.

## 0. Requisitos de la VPS

- Ubuntu 22.04/24.04 o Debian 12 con systemd. El instalador pone lo que falte: Docker Engine, Compose v2, buildx, Nginx, git y openssl. certbot **no** se instala en el sistema: corre en un contenedor fijado por digest.
- Nginx del host: puede tener otros sitios. No se toca `nginx.conf`: se agrega un único sitio `zz-alvarez-finanzas.conf`, que por el nombre se carga último y nunca queda como sitio por defecto de otros proyectos.
- SSH solo con claves; usuario administrativo con sudo. Firewall (ufw **y** el del panel del proveedor): solo 22 (idealmente restringido), 80 y 443. El 80 tiene que quedar abierto: lo usa la renovación del certificado.
- Pertenecer al grupo `docker` equivale a tener root: limitarlo al usuario administrativo.

## 1. Primer despliegue con el instalador (recomendado)

**Si la VPS ya tiene otros proyectos**, primero corré el relevamiento de solo lectura. No instala ni cambia nada, no muestra variables de entorno ni contenidos de `.env` o compose, y tapa contraseñas y tokens. Revisá el reporte antes de compartirlo:

```bash
curl -fsSL https://raw.githubusercontent.com/augussto12/eBalance/implementacion-v1/infra/scripts/vps-survey.sh -o vps-survey.sh
sudo bash vps-survey.sh            # imprime el reporte y lo guarda en /tmp/relevamiento-vps.*.txt (modo 600)
```

Muestra qué ocupa los puertos 80/443 (Nginx del host o un proxy en contenedor: Traefik, Caddy, Nginx Proxy Manager, Coolify, etc.), cómo se publica cada contenedor, las redes y subredes, la memoria y el disco, los certificados, el firewall y las tareas programadas.

```bash
curl -fsSL https://raw.githubusercontent.com/augussto12/eBalance/implementacion-v1/infra/scripts/vps-install.sh -o vps-install.sh
less vps-install.sh                 # revisalo
sudo bash vps-install.sh --inspect  # solo inspecciona y reporta; no cambia nada
tmux new -s alvarez                 # sesión que sobrevive a un corte de SSH (si falta: apt install tmux)
sudo bash vps-install.sh            # dentro de tmux: instalación completa (idempotente: se puede volver a correr)
```

Para ejecutar exactamente lo que revisaste, fijá el commit: `sudo COMMIT=<sha> bash vps-install.sh`. El instalador verifica que el commit pertenezca a la rama y lo usa en modo *detached*. Dentro de `tmux`, un corte de SSH no interrumpe la instalación (`tmux attach -t alvarez` para volver).

**Inspección:** se detiene **antes de cambiar nada**, con un reporte, en estos casos:

- Los puertos 80/443 los usa algo que no es el Nginx del host (Traefik de Coolify, Apache, un contenedor que publica 80/443).
- `nginx -t` ya falla por otro sitio.
- `http(s)://IP` ya sirve otro proyecto, u otro sitio ya es el default del 443 (modo IP).
- Otro sitio usa el mismo nombre.
- La red `172.30.57.0/24` se superpone con otra red de Docker o con una ruta del host.
- Otro proyecto renueva certificados con `certbot --standalone` y habría que instalar Nginx.
- Hay menos de 2,5 GiB de memoria disponible (RAM + swap libres) para compilar. Se puede pasar por alto con `ALLOW_LOW_MEMORY=1`.
- El servidor por defecto de Nginx publica rutas propias (por ejemplo `location /n8n/ { proxy_pass … }`), o `http(s)://` en la dirección elegida ya sirve otra cosa: este sitio se las tomaría. Solo después de revisarlo, `ALLOW_TAKEOVER=1`.
- Docker instalado como snap, Podman en lugar de Docker, Docker anterior a la 23, o sin Compose v2/buildx disponibles. Tampoco sigue si hay un containerd de la distribución en uso sin Docker.
- Menos de ~8 GiB libres en `/` para la primera instalación (~3 GiB en re-ejecuciones).

`--inspect` informa **todos** los bloqueos de una vez y termina con código 1 si hay alguno. También avisa si otros servicios HTTPS de la VPS recibirían la cookie de sesión: no se aísla por puerto. La instalación espera a que apt quede libre (cloud-init, actualizaciones automáticas) y no desinstala paquetes de otros proyectos (`--no-remove`). Con un kernel sin IPv6, instala Nginx sin el `listen [::]` del sitio de serie. `listen [::]` se decide puerto por puerto.

Las re-ejecuciones reconocen el sitio propio y mantienen la dirección guardada en `.env`. Para cambiarla, indicá `PUBLIC_HOST` o `TLS_MODE` explícitamente: el instalador emite el certificado nuevo y recién entonces actualiza `.env` y el sitio. Si ufw está activo, abre 80 y 443 **antes** del desafío ACME. El timer de renovación se instala apenas existe el certificado.

**Dirección pública sin dominio propio** (`TLS_MODE=auto`):

1. Si la VPS tiene un nombre del proveedor que apunta a su IP en el **DNS público** (en Hostinger, `srvNNNN.hstgr.cloud`), se usa ese nombre con un certificado normal de 90 días. Se consulta por DNS sobre HTTPS, no por `/etc/hosts`, y si el nombre tiene un AAAA que no es de la VPS se detiene. `hstgr.cloud` está en la Public Suffix List: cada `srvNNNN` es su propio «sitio». Para forzar la IP: `TLS_MODE=ip`.
2. Si no, se emite un **certificado de Let's Encrypt para la IP**, con disponibilidad general desde el 15/01/2026: perfil `shortlived` de unas 160 h, validación http-01 por el puerto 80 y certbot 5.8 en contenedor. certbot lo renueva a mitad de vida (~80 h antes del vencimiento). El timer `alvarez-certbot.timer` corre dos veces por día y recarga Nginx solo si hubo renovación. `maintenance.sh` alerta si quedan menos de 48 h o si el certificado no valida para la IP. Con IP no hay HSTS.
3. `TLS_MODE=sslip` existe, pero **no se recomienda** para datos financieros. `*.sslip.io` es un DNS de terceros que no está en la Public Suffix List: comparte «sitio» y cupo de certificados con miles de páginas ajenas, incluido phishing.
4. Con dominio propio: `sudo PUBLIC_HOST=finanzas.tu-dominio bash vps-install.sh` (registro A apuntando a la VPS).

**Qué hace al final:** construye las imágenes, migra con el rol migrador, levanta la app y corre los smoke tests y el preflight. Hace un backup y una prueba de restauración aislada, e instala los timers de certificado, backup y mantenimiento. Por último te pide crear tu usuario: la contraseña se escribe oculta y no queda en archivos ni en el historial. El registro sin secretos queda en `/srv/alvarez-finanzas/install.log`.

Después de instalar, guardá en tu gestor de contraseñas, **fuera del servidor**:

- `/srv/alvarez-finanzas/secrets/restic_password`: sin ella, los backups son ilegibles.
- `/srv/alvarez-finanzas/secrets/keyring/current.pfx` y `current.password`: sin ellos no se pueden descifrar los segundos factores de un backup restaurado.

### Despliegue manual (alternativa)

```bash
git clone -b implementacion-v1 https://github.com/augussto12/eBalance.git /srv/alvarez-finanzas/app && cd /srv/alvarez-finanzas/app
cp .env.example .env && nano .env                     # PUBLIC_ORIGIN, SECRETS_DIR, BACKUP_DIR
sudo ./infra/scripts/bootstrap-secrets.sh
sudo ./infra/scripts/install-nginx-site.sh --acme-only finanzas.tu-dominio   # sitio temporal :80 solo para el desafío
sudo ./infra/scripts/certbot.sh issue host finanzas.tu-dominio               # o, para una IP: issue ip 1.2.3.4
sudo CERT_DIR=/etc/alvarez-finanzas/letsencrypt/live/alvarez-finanzas ./infra/scripts/install-nginx-site.sh finanzas.tu-dominio
#   (para una IP agregá DEFAULT_443=1 NO_IPV6=1; el sitio final reemplaza al temporal)
sudo cp infra/systemd/alvarez-certbot.* /etc/systemd/system/ && sudo systemctl daemon-reload && sudo systemctl enable --now alvarez-certbot.timer
./infra/scripts/deploy.sh --first
./infra/scripts/bootstrap-owner.sh --username augusto --display-name "Augusto Álvarez"
```

Abrí la dirección que mostró el instalador, ingresá y configurá el autenticador (QR generado localmente). Guardá los 10 códigos de recuperación. El instalador ya corrió preflight, backup y restore-test; para repetirlos:

```bash
./infra/scripts/preflight.sh           # debe terminar en «Preflight OK» (incluye TLS y check-config)
./infra/scripts/backup.sh
./infra/scripts/restore-test.sh        # debe terminar en resultado=OK ANTES de cargar datos reales
```

`install-nginx-site.sh` corre `nginx -t` antes de tocar nada (si ya falla por otro sitio, no sigue). Instala el sitio con respaldo y, si la validación falla, restaura la versión anterior sin recargar Nginx. Agrega `listen [::]` solo si se lo pide el instalador porque Nginx ya escucha en IPv6.

**HSTS** (solo con un dominio **propio** dedicado; nunca con IP ni con el nombre del proveedor, que comparte el host con otros proyectos HTTP): cuando HTTPS funcione bien, descomentá `Strict-Transport-Security` en `/etc/nginx/sites-available/zz-alvarez-finanzas.conf` y corré `sudo nginx -t && sudo systemctl reload nginx`. No agregues `includeSubDomains` ni `preload` sin revisar todos los subdominios.

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
sudo systemctl enable --now alvarez-certbot.timer alvarez-backup.timer alvarez-maintenance.timer   # el instalador ya lo hace
systemctl list-timers 'alvarez-*'
```

`maintenance.sh` corre todos los días. Purga la auditoría de más de 90 días (con el rol migrador, porque el runtime no puede borrarla) y falla si el disco tiene menos de 2 GiB libres, si algún servicio no está healthy, si no hubo un backup exitoso en las últimas 48 h o si el certificado está por vencer: menos de 48 h en uno de IP, menos de 14 días en uno normal. **Alertas:** una unidad fallida queda en `systemctl --failed` y `journalctl -u alvarez-maintenance`. Para recibir avisos, configurá `OnFailure=` hacia un destino que elijas (correo local, ntfy propio, etc.). Los mensajes no deben llevar contenido financiero. **[no configurado: decisión del propietario]**

**Logs:** cada contenedor usa el driver `local` con rotación por tamaño (10 MB × 5). Eso limita el espacio, **no la antigüedad**. Para una retención estricta de 30 días, usá `logging.driver: journald` y en `/etc/systemd/journald.conf` poné `MaxRetentionSec=30day`. La API no registra cuerpos, cookies, tokens ni parámetros de consulta; Nginx registra rutas sin query string. Auditoría: tabla `audit_events`, 90 días.

**Contraseñas:** PBKDF2-HMAC-SHA512 (formato v3 de Identity), 210.000 iteraciones y sal de 128 bits. Coste medido: unos 156–159 ms por verificación en 4 vCPU Intel Xeon a 2,1 GHz con .NET 10.0.13 (entorno de desarrollo, no la VPS). Cada login consume ese tiempo de CPU; el límite por IP se aplica antes de calcularlo. Para medirlo en una máquina con el SDK: `dotnet test --project tests/unit/AlvarezFinanzas.UnitTests --filter-class AlvarezFinanzas.UnitTests.PasswordHashingTests --output Detailed`. Si cambia la política, el hash se actualiza solo en el siguiente login correcto.

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

**Key ring perdido o reemplazado (falla cerrada).** La primera vez que la API arranca con una base guarda en ella un centinela protegido con Data Protection. En cada arranque lo descifra, junto con los secretos TOTP, con el key ring actual y sin crear claves. Si no puede (volumen `keys` perdido o recreado vacío, key ring de otra instalación, certificado nuevo sin su `previous-*.pfx`), la API **no arranca** y el log muestra `Key ring rechazado: …`. `check-config` y `verify-data` también fallan, y ninguno de los tres genera claves nuevas.

1. Con copia: `./infra/scripts/restore.sh <snapshot|latest> --with-keyring`.
2. Sin copia posible: `./infra/scripts/reset-key-ring.sh`. Pide escribir `ACEPTAR-PERDIDA`, reinicia el segundo factor de los usuarios cuyo secreto ya no se puede descifrar, revoca todas las sesiones y registra un centinela nuevo. Queda auditado como `cli.reset_key_ring`. Los datos financieros no dependen del key ring. **[el comando `reset-key-ring` está probado; el script contra Compose, no]**

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
- **La API no arranca por el key ring:** ver «Key ring perdido o reemplazado» en la sección 5.
- **Perdiste todo:** por SSH, `./infra/scripts/recover-owner.sh --username augusto`. Pide confirmar escribiendo el usuario, define una contraseña nueva (oculta), reinicia el segundo factor y revoca todas las sesiones. Queda auditado como `cli.recover_owner`. No existe recuperación pública por web.

## 9. Incidente (sospecha de acceso indebido)

1. **Restringir:** `docker compose stop web`, o bloquear el sitio en Nginx.
2. **Preservar:** copiá `docker compose logs api web` y los logs de Nginx. Exportá `audit_events` (`docker compose exec db psql -U alvarez_admin -d alvarez -c "\copy audit_events to '/tmp/audit.csv' csv header"`).
3. **Revocar sesiones:** `docker compose exec api dotnet AlvarezFinanzas.Api.dll revoke-sessions`.
4. **Rotar:** contraseña del propietario (`recover-owner.sh`), `rotate-secrets.sh db-app db-migrator keyring-cert restic` y las claves SSH de la VPS.
5. **Evaluar exposición:** revisá la auditoría (logins, exportaciones, importaciones) y los accesos SSH (`journalctl -u ssh`). Un root comprometido puede haber leído memoria, secretos y datos.
6. **Recuperar** desde una copia conocida y buena (`restore.sh`), en una VPS limpia si hubo compromiso del host.
7. **Revisar la causa** y documentarla.
