# Runbook — VPS, despliegue, backups y recuperación

Todos los comandos se probaron en un entorno local equivalente (Docker Compose + Nginx con TLS), salvo los marcados **[no probado …]**. En la VPS real todavía no se ejecutó nada: ver `IMPLEMENTATION-STATUS.md`. El entorno de prueba no tiene systemd: las unidades se validaron con `systemd-analyze verify` y sus scripts se ejecutaron a mano, pero los timers y `OnFailure=` no se vieron disparar **[no probado con systemd]**.

Los scripts de operación se corren como **root** (`sudo`) en la VPS: `.env` y los secretos son de root y los scripts ajustan los permisos que necesita el contenedor de backup, que corre sin root.

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

**Qué hace al final:** construye las imágenes, migra con el rol migrador, levanta la app y corre los smoke tests y el preflight. Hace un backup y una prueba de restauración aislada, e instala los timers de certificado, backup, mantenimiento, chequeo horario y prueba de restauración mensual, con el aviso de fallas (§3). Por último te pide crear tu usuario: la contraseña se escribe oculta y no queda en archivos ni en el historial. El registro sin secretos queda en `/srv/alvarez-finanzas/install.log`.

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
| Actualizar a un commit nuevo | `git pull && sudo ./infra/scripts/deploy.sh` (preflight → build → backup → migración → arranque → smoke) |
| Volver a la versión anterior | `sudo ./infra/scripts/deploy.sh --rollback <tag>` (no revierte migraciones; ver 6). Exige las tres imágenes del tag y no compila nada |
| Solo los smoke tests | `sudo ./infra/scripts/deploy.sh --smoke` |
| Estado / logs | `docker compose ps` · `docker compose logs --tail 100 api` |
| Verificación completa | `sudo ./infra/scripts/preflight.sh` |
| Backup manual | `sudo ./infra/scripts/backup.sh` |
| Prueba de restauración | `sudo ./infra/scripts/restore-test.sh [snapshot]` (el timer la corre el primer domingo de cada mes) |
| Escaneo de vulnerabilidades de las imágenes | `sudo ./infra/scripts/scan-images.sh` (ver `docs/VERSIONES.md`) |

Los smoke tests (al desplegar y en el rollback) verifican `/healthz` y `/api/health`; las cabeceras de seguridad (CSP, `nosniff`, `Referrer-Policy`, `X-Frame-Options`, `Permissions-Policy`, COOP y CORP) en el index, en un asset real del build y en la API; que el asset se sirva como JavaScript con caché inmutable y el index sin caché; que la API sin sesión responda 401 con `no-store`; que un error de la API llegue como JSON y no como el SPA; que no exista el endpoint de datos ficticios; y, fuera del primer despliegue, lo mismo por HTTPS en `PUBLIC_ORIGIN`. No prueban un ingreso con sesión: en el servidor no hay credenciales (hacelo a mano después de cada despliegue).

**Programar las tareas** (el instalador ya lo hace; ajustá `/srv/alvarez-finanzas/app` si el repo está en otra ruta):

```bash
for u in infra/systemd/alvarez-*; do sed "s#/srv/alvarez-finanzas/app#$PWD#g" "$u" | sudo tee "/etc/systemd/system/$(basename "$u")" >/dev/null; done
sudo systemctl daemon-reload
sudo systemctl enable --now alvarez-certbot.timer alvarez-backup.timer alvarez-maintenance.timer \
  alvarez-healthcheck.timer alvarez-restore-test.timer
systemctl list-timers 'alvarez-*'
```

| Timer | Cuándo | Qué hace |
|---|---|---|
| `alvarez-backup` | diario 03:17 | `backup.sh`: base + key ring + configuración, copia externa |
| `alvarez-maintenance` | diario 04:41 | `maintenance.sh` (abajo) |
| `alvarez-healthcheck` | cada hora | `healthcheck.sh`: db/api/web healthy, `/healthz` y `/api/health` por loopback, disco |
| `alvarez-restore-test` | primer domingo del mes, 05:23 | `restore-test.sh` (§5) |
| `alvarez-certbot` | 2 por día | renovación del certificado |

`maintenance.sh` purga la auditoría de más de 90 días (con el rol migrador, porque el runtime no puede borrarla) y falla si hay menos de 2 GiB libres en el disco del repo, en el de los datos de Docker o en el de `BACKUP_DIR`; si systemd dice que el reloj no está sincronizado por NTP (los códigos TOTP dependen de la hora; `sudo timedatectl set-ntp true`); si algún servicio no está healthy; si no hubo un backup exitoso en las últimas 48 h, ni una copia externa si está configurada; o si el certificado está por vencer: menos de 48 h en uno de IP, menos de 14 días en uno normal. Avisa, sin fallar, si no hay copia externa o si quedó una rotación de restic sin confirmar más de 7 días. `preflight.sh` hace las mismas verificaciones de disco, reloj y copia externa.

**Alertas.** Cada servicio `alvarez-*` tiene `OnFailure=alvarez-alert@%n.service`, que corre `alert.sh`: registra la falla en el journal (`journalctl -t alvarez-alert`) y en `/var/log/alvarez-finanzas/alerts.log`, y **solo si lo configurás en `.env`** avisa a un destino que elijas. El texto es fijo (unidad, servidor y hora): nunca lleva contenido financiero ni extractos de logs. Por defecto no se envía nada fuera del servidor.

- `ALERT_WEBHOOK_URL=https://…`: recibe un POST de texto plano (por ejemplo, un tema de ntfy propio). Si necesita token, poné la cabecera en `SECRETS_DIR/alert-webhook.headers` (p. ej. `Authorization: Bearer …`, modo 400). Solo HTTPS; con una CA propia, `ALERT_WEBHOOK_CA_FILE`.
- `ALERT_EMAIL=dirección`: usa el `sendmail` local, si hay uno instalado y configurado.
- `ALERT_REPEAT_HOURS` (6 por defecto): no repite el aviso de la misma unidad antes de ese plazo. El chequeo horario hace que un servicio caído se note en menos de una hora.
- Probalo con `sudo ./infra/scripts/alert.sh --test`. Probado contra un receptor HTTPS local (`infra/tests/alerts.sh`) **[no probado con un destino real]**.

**Logs.** Cada contenedor usa el driver `local` con rotación por tamaño (10 MB × 5): eso limita el espacio, **no la antigüedad**. Para una retención estricta de 30 días, usá `logging.driver: journald` y en `/etc/systemd/journald.conf` poné `MaxRetentionSec=30day`. El log de Nginx del host (`/var/log/nginx/alvarez-finanzas.*.log`) lo rota el logrotate de la distribución (en Debian/Ubuntu, 14 días). La API no registra cuerpos, cookies, tokens ni parámetros de consulta. Los dos Nginx (el del host y el del contenedor `web`) usan el formato `alvarez_min`: método, ruta **sin query string**, estado, tamaño y duración, sin User-Agent ni Referer, y la IP reducida a /24 (IPv4) o a sus primeros bloques (IPv6). Lo comprueba `infra/tests/nginx-logs.sh`. Límite: el *error log* de Nginx no tiene formato configurable y, ante un error del upstream (API caída), incluye la petición completa y la IP del cliente. El sitio del host se instala aparte: después de actualizar desde una versión anterior, reinstalalo (`sudo CERT_DIR=/etc/alvarez-finanzas/letsencrypt/live/alvarez-finanzas ./infra/scripts/install-nginx-site.sh NOMBRE --force`, con `DEFAULT_443=1 NO_IPV6=1` si es por IP, o volvé a correr el instalador); `preflight.sh` avisa si el sitio instalado es viejo. Auditoría: tabla `audit_events`, 90 días.

## 4. Backups

- `backup.sh` genera un `pg_dump` consistente (rol migrador) más el key ring, cifrados por restic **antes** de escribir en `BACKUP_DIR`. Retención: últimas 10 + 7 diarios + 4 semanales + 3 mensuales. Al final ejecuta `restic check` sobre una muestra. `BACKUP_DIR/backup.log` registra cada backup y el resultado de la copia externa (`offsite=OK|FALLA|no-configurada`).
- También respalda la **configuración no secreta** con la etiqueta `config`: `.env`, el sitio de Nginx instalado y su snippet, las unidades `alvarez-*` de systemd, `.release-history` y el commit desplegado. Los secretos no van ahí. Para revisarla: `sudo ./infra/scripts/restore-config.sh [snapshot] [DESTINO]` (la extrae en un directorio nuevo; no instala nada).
- El contenedor de backup corre **sin root** (uid 1654, el dueño del key ring) y sin capacidades efectivas. `backup.sh` deja `BACKUP_DIR` en `root:1654 750`, el repositorio local de `1654` y `restic_password` en `root:1654 440`. Solo la extracción de una restauración (`restore.sh`, `restore-test.sh`, `restore-config.sh`) corre como root dentro del contenedor (`--user 0`): escribe en un directorio temporal del host y conserva dueños y permisos del key ring. Probado con la imagen nueva: backup, copia externa, rotación, `restore-test.sh`, `restore.sh --with-keyring` y `restore-config.sh`; con la imagen de backup del release anterior (después de un rollback): backup, copia externa con rotación y `restore-test.sh`.
- El backup **no** incluye el certificado del key ring: está en tu gestor de contraseñas. No guardes la única clave junto al único backup.

### Copia fuera de la VPS (obligatoria antes de cargar datos reales)

Sin ella, el único backup (y su contraseña) quedan en el mismo servidor que los datos: `preflight.sh` y `maintenance.sh` lo avisan.

1. Elegí un destino privado con un backend **nativo** de restic: S3 o compatible (`s3:https://…`), Backblaze (`b2:`), Azure (`azure:`), Google Cloud Storage (`gs:`), OpenStack (`swift:`) o un rest-server propio (`rest:https://…`). **sftp no está soportado**: la imagen de backup no trae cliente SSH ni se le montan claves.
2. En `.env`: `OFFSITE_RESTIC_REPOSITORY=s3:https://s3.ejemplo.com/mi-bucket/alvarez` (sin credenciales en la URL).
3. Las credenciales van en `SECRETS_DIR/restic-offsite.env`, una por línea `NOMBRE=valor` (sin `export`; las comillas que envuelven el valor se quitan):

   ```bash
   sudo install -m 440 -o root -g 1654 /dev/null /srv/alvarez-finanzas/secrets/restic-offsite.env
   sudo nano /srv/alvarez-finanzas/secrets/restic-offsite.env
   #   AWS_ACCESS_KEY_ID=…            (S3)       B2_ACCOUNT_ID=… / B2_ACCOUNT_KEY=…   (b2:)
   #   AWS_SECRET_ACCESS_KEY=…                   RESTIC_REST_USERNAME=… / RESTIC_REST_PASSWORD=…   (rest:)
   ```

   El archivo se monta de solo lectura en el contenedor y se lee **sin ejecutarse**. Solo acepta variables de backends (`AWS_*`, `B2_*`, `AZURE_*`, `OS_*`, `ST_*`, `RESTIC_REST_USERNAME`, `RESTIC_REST_PASSWORD`) y de proxy: no puede cambiar el repositorio ni la contraseña de restic.
4. `sudo ./infra/scripts/backup.sh`. La primera vez crea el repositorio externo con la **misma contraseña de restic** y los mismos parámetros de fragmentación; después copia las instantáneas nuevas (`restic copy`), poda con la misma retención y corre `restic check`. Si el destino no responde, restic reintenta unos 15 minutos antes de rendirse. Si la copia falla, el backup local queda hecho, pero `backup.sh` termina con error (→ alerta) y `maintenance.sh` falla si no hubo copia externa en 48 h. Una restauración no espera al destino externo (`BACKUP_LOCAL_ONLY=1`).
5. `sudo ./infra/scripts/backup.sh --verify-offsite`: descarga el último dump del repositorio externo, comprueba que sea un dump válido (`pg_restore --list`) y que coincida con el local. Hacelo al configurarlo y una vez por mes.

**Destino de solo agregar (recomendado):** con `rest-server --append-only` o un bucket con bloqueo de objetos, poné `OFFSITE_PRUNE=0` en `.env`. Así, un atacante con root en la VPS no puede borrar las copias externas. La poda (`restic forget --prune`) la hacés de vez en cuando desde otra máquina de confianza.

Probado con un rest-server local con usuario y contraseña, a través del archivo de credenciales (`sudo infra/tests/offsite-backup.sh`): creación del repositorio, copia, copia incremental, verificación del dump externo, credenciales incorrectas (el backup local queda, la copia falla y `backup.sh` sale con error) y variables no admitidas. **[no probado con un proveedor real ni con `OFFSITE_PRUNE=0`]**

## 5. Restauración

**Prueba aislada (no toca producción):** `sudo ./infra/scripts/restore-test.sh` (también la corre `alvarez-restore-test.timer` el primer domingo de cada mes; si falla, avisa como cualquier otra unidad). Levanta una base y una API en una red interna sin salida y restaura el dump y el key ring. Verifica conteos, sumas por moneda, consistencia de cuotas, descifrado de los secretos TOTP, que la API arranque y que Data Protection funcione. Revoca las sesiones restauradas y compara con producción. El resultado (fecha, duración, OK/FALLA) queda en `BACKUP_DIR/restore-tests.log`. El script no prueba un ingreso interactivo; hacelo a mano una vez por mes:

1. Restaurá en una máquina de prueba con `restore.sh`.
2. Ingresá con tu contraseña y tu autenticador.
3. Revisá Resumen y Movimientos.

**Restauración real:** `sudo ./infra/scripts/restore.sh <snapshot|latest> [--with-keyring]`.

- Pide escribir `RESTAURAR` (y `CLAVES` si restaurás el key ring) y fija el ID de la instantánea **antes** de hacer un backup de seguridad del estado actual (sin poda).
- Detiene la API y la web, restaura con `pg_restore` dejando las tablas a nombre del rol migrador, re-aplica permisos, revoca todas las sesiones y vuelve a levantar todo.
- Usá `--with-keyring` solo si se perdió el volumen de claves.
- Nunca se ejecuta automáticamente.
- La configuración del servidor (`.env`, sitio de Nginx, unidades) se recupera aparte, a mano, con `restore-config.sh` (§4).

RPO objetivo ≤ 24 h (backup diario). RTO objetivo ≤ 4 h: en el entorno local, restaurar llevó menos de un minuto con pocos datos. Medilo en la VPS.

## 6. Migraciones y rollback

Las migraciones se aplican una sola vez por despliegue (`migrate`, rol migrador con DDL). La API usa el rol runtime: solo DML, sin UPDATE/DELETE sobre la auditoría y sin DDL. Para migraciones futuras, usar expand/contract: agregar sin romper la versión anterior y quitar en un despliegue posterior. Si una migración no es compatible hacia atrás, el rollback es `restore.sh` del backup previo al deploy (con pérdida de lo cargado después), dentro de una ventana de mantenimiento.

## 7. Rotación de secretos

Acepta varios objetivos en una misma ejecución; los valida todos antes de empezar y reinicia la API una sola vez al final (también si un objetivo posterior falla).

```bash
sudo ./infra/scripts/rotate-secrets.sh db-app            # contraseña del rol runtime + reinicio de la API
sudo ./infra/scripts/rotate-secrets.sh db-migrator       # contraseña del rol migrador (backup y migraciones la leen al correr)
sudo ./infra/scripts/rotate-secrets.sh keyring-cert      # certificado nuevo; el anterior queda como previous-*.pfx
sudo ./infra/scripts/rotate-secrets.sh restic            # fase 1: clave nueva en el repositorio local Y en el externo
sudo ./infra/scripts/rotate-secrets.sh restic-confirm    # fase 2, después de guardar la nueva fuera del servidor
sudo ./infra/scripts/rotate-secrets.sh db-app db-migrator keyring-cert restic   # todo junto (incidente)
```

- **Base de datos:** la contraseña nueva se escribe en el archivo de secretos (mismo inodo) y `psql` la lee de ese archivo montado: no aparece en argumentos ni se arma SQL con ella. Si `ALTER ROLE` falla, se restituye la anterior.
- **restic, sin perder nunca el acceso:** la fase 1 comprueba que cada repositorio (local y, si está configurado, el externo) abra con la contraseña vigente (si el externo no responde en 90 s, no cambia nada); agrega la clave nueva a todos (si uno falla, deshace lo hecho y no cambia nada); verifica que la nueva abra todos; recién entonces la deja como vigente y guarda la anterior en `restic_password.previous`, que sigue abriendo los backups. Guardá la nueva en tu gestor de contraseñas y corré `restic-confirm`: pide escribir `GUARDADA` y quita la clave anterior de ambos repositorios. `preflight.sh` avisa mientras haya una rotación sin confirmar, y `maintenance.sh` a los 7 días.
- Cambiar la contraseña de restic **no** cambia la clave maestra del repositorio: si alguien pudo copiar el repositorio **y** la contraseña, esos backups siguen expuestos. En ese caso, empezá repositorios nuevos (mové `BACKUP_DIR/restic` y usá otra ruta externa) después de rotar.
- No borres `previous-*.pfx` mientras existan claves o backups que lo necesiten (se recomienda al menos un año). Actualizá tu gestor de contraseñas después de cada rotación.

Probadas en el entorno de prueba: cada objetivo por separado y los cuatro juntos, con la API healthy después, backup con la contraseña nueva del migrador y `restore-test.sh` con el certificado rotado; la rotación de restic en sus dos fases contra el repositorio local y uno externo (rest-server), comprobando que la anterior abre todo hasta confirmar y nada después.

## 8. Recuperación de acceso

- **Perdiste el celular, tenés códigos de recuperación:** en la pantalla del código, «Usar un código de recuperación». Esto cierra las demás sesiones. Después: Configuración → Seguridad → «Reconfigurar autenticador» y «Generar códigos nuevos».
- **Perdiste todo:** por SSH, `./infra/scripts/recover-owner.sh --username augusto`. Pide confirmar escribiendo el usuario, define una contraseña nueva (oculta), reinicia el segundo factor y revoca todas las sesiones. Queda auditado como `cli.recover_owner`. No existe recuperación pública por web.

## 9. Incidente (sospecha de acceso indebido)

Desde el directorio de la app (`cd /srv/alvarez-finanzas/app`), como root:

1. **Restringir:** `docker compose stop web`, o bloquear el sitio en Nginx. (El chequeo horario va a avisar que `web` está caído: es esperable.)
2. **Preservar** (en un directorio solo de root; después copialo fuera del servidor):

   ```bash
   dir=/root/incidente-$(date -u +%Y%m%dT%H%M%SZ); install -d -m 700 "$dir"
   docker compose logs --no-color --timestamps api web db > "$dir/contenedores.log"
   cp -p /var/log/nginx/alvarez-finanzas.* /var/log/alvarez-finanzas/alerts.log "$dir/" 2>/dev/null
   journalctl --since "-14 days" -u ssh -u 'alvarez-*' > "$dir/journal.log"
   docker compose exec -T db psql -U alvarez_admin -d alvarez -c "\copy audit_events to stdout with csv header" > "$dir/audit_events.csv"
   ```

   `\copy … to stdout` escribe en la salida de `psql`, que queda directamente en el archivo del host (nada en `/tmp` del contenedor).
3. **Revocar sesiones:** `docker compose exec api dotnet AlvarezFinanzas.Api.dll revoke-sessions`.
4. **Rotar:** contraseña del propietario (`recover-owner.sh`); `sudo ./infra/scripts/rotate-secrets.sh db-app db-migrator keyring-cert restic` y, después de guardar la contraseña nueva de restic fuera del servidor, `sudo ./infra/scripts/rotate-secrets.sh restic-confirm` (§7; si el repositorio pudo copiarse, empezá uno nuevo); las credenciales del destino externo y del aviso de alertas en sus proveedores (y en `SECRETS_DIR`); y las claves SSH de la VPS.
5. **Evaluar exposición:** revisá la auditoría (logins, exportaciones, importaciones) y los accesos SSH (`journalctl -u ssh`). Un root comprometido puede haber leído memoria, secretos y datos.
6. **Recuperar** desde una copia conocida y buena (`restore.sh`), en una VPS limpia si hubo compromiso del host.
7. **Revisar la causa** y documentarla.

## 10. Pruebas de los scripts de operación

| Prueba | Qué comprueba | Dónde corre |
|---|---|---|
| `infra/tests/nginx-logs.sh [IMAGEN_WEB]` | Los dos Nginx registran sin query string, sin User-Agent y con la IP reducida | CI y local (contenedores sin red) |
| `infra/tests/alerts.sh` | `alert.sh`: solo registra sin destino; con destino envía el texto fijo y el token; no repite; rechaza `http://` | CI y local |
| `infra/scripts/scan-images.sh [TAG]` | Trivy: sin HIGH/CRITICAL corregibles fuera de `infra/trivy-ignore.yaml` | CI (cada push y semanal) y local |
| `sudo infra/tests/offsite-backup.sh [--rotate-restic]` | Copia externa con credenciales contra un rest-server local; con `--rotate-restic`, la rotación completa | Sobre una instalación de prueba (rota secretos reales: no usar en producción con `--rotate-restic`) |

En CI también corren `shellcheck` (fijado por digest) sobre todos los scripts y `systemd-analyze verify` sobre las unidades.
