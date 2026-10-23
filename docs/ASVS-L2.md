# Autoevaluación OWASP ASVS 4.0.3 — Nivel 2

**Qué es y qué no es.** Esta es una autoevaluación hecha por quien implementó la aplicación: no la hizo un tercero independiente. **No es una certificación**: OWASP no certifica aplicaciones contra ASVS. Tampoco es una auditoría ni una prueba de penetración. `docs/03-SEGURIDAD.md:3` pide usar ASVS nivel 2 como referencia de revisión, sin afirmar certificación, y eso es lo que hace este documento. Si algo de esto se presenta a terceros, hay que presentarlo como autoevaluación.

**Qué se evaluó.** El código del commit `6957687` de la rama `implementacion-v1`, el 23/10/2026: API (`src/api`), SPA (`src/web`), Compose, Nginx, Dockerfiles, scripts (`infra/`) y CI. La VPS real **no se evaluó** porque todavía no hay despliegue (ver `docs/IMPLEMENTATION-STATUS.md`): todo lo que depende del servidor real figura como pendiente o parcial. Si se integran cambios posteriores, los estados de esta tabla pueden quedar desactualizados; hay que revisarlos.

**Método:**

1. **Lectura del código**, no de los documentos de estado. Cada evidencia cita `archivo:línea`.
2. **Pruebas automatizadas** ejecutadas el 23/10/2026 en este commit:
   - `dotnet test --project tests/unit/AlvarezFinanzas.UnitTests`: **45/45**;
   - `dotnet test --project tests/integration/AlvarezFinanzas.IntegrationTests`, contra PostgreSQL 18 real con Testcontainers: **28/28**;
   - `npx vitest run` en `src/web`: **19/19**.
3. **Verificación manual reproducible** con un programa descartable, fuera del repo. Arma la API real con `Program.Build` en entorno `Production` y observa:
   - los parámetros de hash;
   - el key ring;
   - las respuestas HTTP que no necesitan base de datos.

   Las observaciones se citan como **[B-n]** y están en el anexo B. El costo del hash está medido en el anexo A.
4. **No se ejecutaron:**
   - el E2E de Playwright: se cita como prueba existente, con su archivo y línea;
   - el stack de Compose;
   - ninguna herramienta externa (testssl, nmap, ZAP).

**Estados:**

| Estado | Significado |
|---|---|
| **cumple** | Implementado. La evidencia está en el repo: código o configuración, con prueba automatizada si se indica. |
| **parcial** | Implementado en parte, o con una brecha conocida que se describe. |
| **pendiente** | No implementado, o depende de algo que todavía no se hizo (por ejemplo, la VPS). |
| **no aplica** | La funcionalidad no existe en la aplicación, con la razón. |

**Abreviaturas de rutas:**

| Abreviatura | Ruta |
|---|---|
| `API/` | `src/api/AlvarezFinanzas.Api/` |
| `IT` | prueba de integración en `tests/integration/AlvarezFinanzas.IntegrationTests/` |
| `UT` | prueba unitaria en `tests/unit/AlvarezFinanzas.UnitTests/MoneyAndCalendarTests.cs` |
| `WEB/` | `src/web/src/` |
| `E2E` | `tests/e2e/flows.spec.ts` |
| `NGX/` | `infra/nginx/` |

## Resumen

Se listan los 259 requisitos de nivel 2 de ASVS 4.0.3, incluidos los que no aplican.

| Capítulo | cumple | parcial | pendiente | no aplica | Total |
|---|---|---|---|---|---|
| V1 Arquitectura | 19 | 17 | 1 | 1 | 38 |
| V2 Autenticación | 25 | 7 | 7 | 14 | 53 |
| V3 Sesiones | 14 | 2 | 0 | 2 | 18 |
| V4 Control de acceso | 8 | 0 | 0 | 1 | 9 |
| V5 Validación y codificación | 17 | 2 | 0 | 11 | 30 |
| V6 Criptografía almacenada | 6 | 4 | 1 | 2 | 13 |
| V7 Errores y logs | 6 | 6 | 0 | 0 | 12 |
| V8 Protección de datos | 8 | 5 | 1 | 1 | 15 |
| V9 Comunicaciones | 1 | 4 | 1 | 1 | 7 |
| V10 Código malicioso | 3 | 0 | 1 | 1 | 5 |
| V11 Lógica de negocio | 5 | 1 | 2 | 0 | 8 |
| V12 Archivos | 7 | 1 | 0 | 7 | 15 |
| V13 API | 7 | 2 | 0 | 4 | 13 |
| V14 Configuración | 12 | 7 | 2 | 2 | 23 |
| **Total** | **138** | **58** | **16** | **47** | **259** |

**Brechas principales**, en orden sugerido:

1. **Antes de cargar datos reales** (gates de `AGENTS.md`): HTTPS de producción validado, HSTS y verificación TLS externa (9.1.1, 9.1.2, 9.1.3, 14.4.5).
2. **Logs:** el Nginx del contenedor `web` registra la línea completa del request, con las búsquedas (`?q=`) y la IP completa (7.1.2, 8.3.1).
3. **Disponibilidad:** el bloqueo de cuenta lo puede provocar cualquiera que conozca el usuario (2.2.1, riesgo residual R1).
4. **Truncado silencioso:** el frontend corta sin aviso una contraseña pegada de más de 128 caracteres (2.1.3).
5. **Sin aviso al usuario** de cambios en sus credenciales o factores (2.2.3, 2.5.5). No hay correo en v1, por especificación.
6. **Sin alertas ni monitoreo** de abuso (7.2.x, 8.1.4, 11.1.7, 11.1.8).
7. **Códigos de recuperación** de 80 bits con hash sin sal individual (2.6.2).
8. **Content-Type inesperado:** recibe 404 en lugar de 415, por la ruta comodín (13.1.5, 13.2.5).
9. **Tráfico interno del host sin TLS:** Nginx → web → API → PostgreSQL (1.9.1, 9.2.2).
10. **Cadena de suministro:** sin escaneo de imágenes ni SBOM formal (14.2.1, 14.2.5).

## Componentes y fronteras de confianza

Sirve de evidencia para 1.1.4, 1.1.5 y 1.11.1. Complementa `docs/02-ARQUITECTURA-Y-DATOS.md`.

| Componente | Función de seguridad | Expuesto a | Código |
|---|---|---|---|
| Navegador (SPA React) | Solo presentación. Ningún control de acceso vive acá. | Usuario (no confiable) | `WEB/` |
| Nginx del host | Termina TLS; responde 421 a Host ajenos; `limit_req` en el login; logs mínimos sin query string | Internet: 80 y 443 | `NGX/finanzas.conf:9-60` |
| Contenedor `web` (nginx-unprivileged) | Sirve el SPA con CSP y cabeceras; reenvía `/api` a la API; fija `X-Forwarded-For` con la IP ya validada | Solo `127.0.0.1:8088` | `NGX/web/default.conf`, `compose.yaml:83-95` |
| Contenedor `api` (ASP.NET Core 10, no root) | Autoridad de dinero, propiedad, permisos, sesiones, MFA, CSRF, límites y auditoría | Red `frontend` (172.30.57.11), sin puertos publicados | `API/Program.cs`, `compose.yaml:54-81` |
| Contenedor `db` (PostgreSQL 18) | Roles separados: `alvarez_migrator` (DDL) y `alvarez_app` (DML sin UPDATE/DELETE en auditoría); FK compuestas por propietario; checks de importes | Red `backend` (`internal: true`) | `compose.yaml:17-36,119-121`, `infra/docker/postgres/01-roles.sh`, `API/Infrastructure/Cli.cs:70-94` |
| `migrate` y CLI (perfil `ops`) | Migraciones con el rol migrador; comandos de operador por SSH: `bootstrap-owner`, `recover-owner`, `revoke-sessions`, `purge-audit`, `check-config`, `verify-data` | Operador con SSH y grupo `docker` | `API/Infrastructure/Cli.cs` |
| `backup` (perfil `ops`) | `pg_dump` y key ring cifrados por restic antes de escribirse; copia off-site opcional | Redes `backend` y `egress` | `infra/docker/backup-entrypoint.sh`, `compose.yaml:97-117` |
| Servicios remotos | Let's Encrypt (ACME http-01, certbot en contenedor); repositorio restic off-site (opcional, no probado); GitHub (código y CI); registros de imágenes y paquetes, solo al construir | — | `infra/scripts/certbot.sh`, `infra/scripts/backup.sh:56-66`, `.github/workflows/ci.yml` |

**Flujos:**

- **Uso normal:** navegador → HTTPS → Nginx del host → HTTP por loopback → `web` → HTTP por la red Docker `frontend` → `api` → protocolo PostgreSQL con SCRAM, **sin TLS**, por la red `backend` interna → `db`.
- **Backup:** `db` → `pg_dump` (rol migrador) → restic (AES-256) → `BACKUP_DIR` y, opcionalmente, el repositorio off-site.

**Fronteras de confianza:**

1. Internet ↔ Nginx del host, protegida por TLS.
2. Host ↔ contenedores: root o el grupo `docker` controlan todo.
3. `web` ↔ `api`: la API acepta `X-Forwarded-For` solo de 172.30.57.10.
4. `api` ↔ `db`: privilegios por rol.
5. Operador por SSH ↔ host.

## Clasificación de datos

Sirve de evidencia para 1.8.1, 1.8.2 y 8.3.4.

| Dato | Dónde | Clase | Protección aplicada |
|---|---|---|---|
| Contraseña del propietario | Nunca se guarda | Secreto de autenticación | Hash PBKDF2-HMAC-SHA512 con 210.000 iteraciones (anexo A). La CLI la lee oculta y nunca la toma de argumentos (`API/Infrastructure/Cli.cs:300-334`). |
| Secreto TOTP | `AspNetUsers.totp_secret_protected` | Secreto de autenticación | Data Protection (AES-256-CBC + HMAC-SHA256) con el propósito `AlvarezFinanzas.Totp.v1` (`API/Infrastructure/Auth/Security.cs:31-41`). |
| Códigos de recuperación | `recovery_codes.code_hash` | Secreto de autenticación | SHA-256; se muestran una sola vez (`Security.cs:71-112`). |
| Referencia de sesión | Cookie `__Host-alvarez-session`; en la base, su SHA-256 y el ticket cifrado | Secreto de sesión | 256 bits aleatorios; ticket cifrado con Data Protection; revocable (`API/Infrastructure/Auth/DbTicketStore.cs:55,83-97`). |
| Key ring de Data Protection | Volumen `keys` (`/keys`, 0700) | Clave | Cifrado con un certificado RSA-3072 (anexo B-6). |
| Certificado del key ring y su contraseña; contraseñas de la base; contraseña de restic | `SECRETS_DIR` del host (0400/0440, directorio 0500), montado de solo lectura | Clave o secreto | Permisos de archivo; no es una bóveda (`infra/scripts/bootstrap-secrets.sh:9-46`, `compose.yaml:5`). |
| Datos financieros: importes, conceptos, notas, nombres de cuentas y categorías, fechas, `last4` | PostgreSQL | Confidencial (financiero personal) | Base sin puerto publicado; rol runtime mínimo; backups cifrados. **En claro dentro de la base** (riesgo residual R5). Nunca PAN completo, CVV ni claves bancarias (`API/Infrastructure/AppDbContext.cs:43,49`). |
| Usuario y nombre para mostrar | `AspNetUsers` | Personal | Base |
| Metadatos de sesión | `user_sessions` | Personal minimizado | User-Agent truncado a 160 caracteres; IP reducida a /24 o /48 (`Security.cs:14-26`). |
| Auditoría | `audit_events` | Operativo | Sin importes ni conceptos; IP minimizada; retención de 90 días (`API/Infrastructure/Audit.cs:7-29`, `Cli.cs:96-105`). |
| Logs de contenedores y de Nginx | Driver `local` de Docker; `/var/log/nginx` del host | Operativo con datos personales | Rotación por tamaño. El Nginx interno guarda IP completa y query string (ver 7.1.2). |
| Vista previa de importación | `import_batches.normalized_payload` (jsonb) | Confidencial temporal | Caduca a los 30 min y se borra al confirmar o caducar (`API/Api/Middleware.cs:108-110`). |
| Archivos de importación y exportación | Dispositivo del usuario; el servidor guarda solo el hash del archivo | Confidencial | Descarga autenticada y `no-store` (`API/Api/ImportExportEndpoints.cs:509-511`). |

## Inventario de claves y secretos

Sirve de evidencia para 1.6.x, 2.10.x y 6.4.x. Es también el registro de la gestión de claves que pide `AGENTS.md`.

| Clave o secreto | Uso | Almacenamiento | Rotación | Copia |
|---|---|---|---|---|
| Claves maestras de Data Protection | Tickets de sesión, cookies, tokens antiforgery, secretos TOTP | `keys:/keys`, XML cifrado con el certificado | Automática cada 90 días; las anteriores se conservan [B-6] | En el backup restic (cifrado); un `/keys` sin claves no se respalda (`backup-entrypoint.sh:28-42`) |
| Certificado del key ring (RSA-3072, PFX AES-256-CBC, 10 años) y su contraseña | Cifra el key ring | `SECRETS_DIR/keyring` (0400; directorio 0500), montado en `/run/keyring:ro` | `rotate-secrets.sh keyring-cert`; los `previous-*.pfx` siguen descifrando (`API/Program.cs:96-108`) y **no se borran nunca**: el key ring conserva las claves cifradas con cada certificado y los backups anteriores dependen de ellas (`docs/RUNBOOK.md` §7) | Gestor de contraseñas del propietario, **fuera** del servidor: el actual y todos los `previous-*.pfx` con su contraseña; no van en el backup |
| Contraseñas de `alvarez_app` y `alvarez_migrator` | Conexión a la base | `SECRETS_DIR` (0440) | `rotate-secrets.sh db-app` y `db-migrator` | Gestor de contraseñas, opcional |
| Contraseña de `alvarez_admin` (superusuario) | Administración de la base | `SECRETS_DIR` (0400) | **Sin script** de rotación | Gestor de contraseñas, opcional |
| Contraseña de restic | Cifra los backups | `SECRETS_DIR/restic_password` (0400, root) | `rotate-secrets.sh restic` (no ejecutado según `IMPLEMENTATION-STATUS.md`) | Gestor de contraseñas, obligatorio |
| Credenciales off-site | Repositorio restic remoto | `SECRETS_DIR/restic-offsite.env` | Manual | Gestor de contraseñas |
| Clave HMAC de los limitadores | Seudonimiza el usuario en el límite por cuenta | Memoria; aleatoria en cada arranque (`Security.cs:124`) | En cada reinicio | No aplica |
| Clave privada TLS | HTTPS | Directorio de certbot en el host | Renovación automática (`alvarez-certbot.timer`) | Se reemite |

Los secretos de 48 caracteres salen de `openssl rand` (`bootstrap-secrets.sh:16`).

## V1 Arquitectura, diseño y modelado de amenazas

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 1.1.1 | Ciclo de desarrollo seguro | parcial | Hay requisitos de seguridad escritos (`docs/03-SEGURIDAD.md`), reglas para quien desarrolla (`AGENTS.md`), CI con pruebas, auditoría de dependencias y gitleaks (`.github/workflows/ci.yml:17-63`) y revisiones adversariales registradas (`docs/IMPLEMENTATION-STATUS.md:128-156`). No hay un proceso de SDLC formal documentado. |
| 1.1.2 | Modelado de amenazas en cada cambio de diseño | parcial | Hay una lista de amenazas única (`docs/03-SEGURIDAD.md:5-6`); no se modela por cambio. |
| 1.1.3 | Restricciones de seguridad en cada historia | parcial | Las restricciones están por área, con pruebas negativas obligatorias (`docs/03-SEGURIDAD.md:53-54`), pero no por historia de usuario. |
| 1.1.4 | Fronteras de confianza, componentes y flujos documentados | cumple | Sección «Componentes y fronteras de confianza» de este documento; `docs/02-ARQUITECTURA-Y-DATOS.md:3-8`. |
| 1.1.5 | Arquitectura de alto nivel y servicios remotos analizados | cumple | Ídem, incluidos los servicios remotos. |
| 1.1.6 | Controles de seguridad centralizados y reutilizables | cumple | Políticas únicas (`API/Program.cs:144-166`); validación (`API/Api/ApiSupport.cs:8-77`); referencias por propietario (`API/Api/EntryEndpoints.cs:175-192`); CSRF (`API/Api/Middleware.cs:43-74`); límites (`API/Infrastructure/Auth/Security.cs:118-169`); auditoría (`API/Infrastructure/Audit.cs`). |
| 1.1.7 | Guía de codificación segura disponible | cumple | `AGENTS.md`, sección «Trabajo»: sin `dangerouslySetInnerHTML`, eval, SQL interpolado ni criptografía casera, entre otras reglas; `docs/03-SEGURIDAD.md`. |
| 1.2.1 | Cuentas de SO únicas y de bajo privilegio por componente | parcial | `api` corre como `APP_UID` 1654 (`infra/docker/api.Dockerfile:30`); `web` usa nginx-unprivileged (`infra/docker/web.Dockerfile:4`); `cap_drop: ALL` y `no-new-privileges` (`compose.yaml:8-14`). El contenedor `backup` corre como uid 1654 (`infra/docker/backup.Dockerfile:15`, `compose.yaml:106`); solo la extracción de restauraciones usa `--user 0` con `DAC_OVERRIDE`, `CHOWN` y `FOWNER` para escribir en un directorio temporal del host y conservar dueños del key ring (`compose.yaml:110-113`, `infra/scripts/restore-test.sh:38`). |
| 1.2.2 | Comunicación entre componentes autenticada y con mínimo privilegio | parcial | API → base: SCRAM-SHA-256 con un rol solo DML (`compose.yaml:25`, `Cli.cs:82-90`). `web` → `api` y host → `web` no se autentican: dependen del aislamiento de red y loopback. |
| 1.2.3 | Un mecanismo de autenticación único y probado | cumple | ASP.NET Core Identity, cookies con ticket en servidor y TOTP (`API/Program.cs:115-142`); eventos auditados (`API/Api/AuthEndpoints.cs:134-157`). |
| 1.2.4 | Todas las vías de autenticación con la misma fortaleza | cumple | Solo hay contraseña + TOTP; la alternativa al TOTP son códigos de recuperación de un uso. Sin registro ni recuperación pública (`AuthEndpoints.cs:28-54`); la CLI exige SSH. |
| 1.4.1 | Control de acceso en puntos de aplicación confiables | cumple | Todo control de acceso está en la API: políticas y filtros por `OwnerId` en cada consulta. El frontend no decide permisos. |
| 1.4.4 | Un único mecanismo de control de acceso | parcial | La autorización por política es única (`API/Program.cs:144-166,249`). El filtro por propietario, en cambio, se repite en cada consulta, sin un filtro global de EF. Lo respaldan las FK compuestas (`API/Infrastructure/AppDbContext.cs:231-237`) y las pruebas con dos usuarios. |
| 1.4.5 | Control por atributo o dato, no solo por rol | cumple | Cada registro se autoriza por `OwnerId` derivado de la sesión (`API/Infrastructure/Auth/Security.cs:178-179`); IT `User_b_cannot_read_modify_relate_or_aggregate_user_a_records`. |
| 1.5.1 | Requisitos de entrada y salida definidos | cumple | `docs/02-ARQUITECTURA-Y-DATOS.md:30-57`; `docs/API.md`. |
| 1.5.2 | Sin serialización de objetos con clientes no confiables | cumple | Solo DTO JSON tipados. El ticket de sesión serializado nunca sale del servidor: al cliente viaja solo la referencia protegida (`DbTicketStore.cs:83-102`). |
| 1.5.3 | Validación de entrada en la capa confiable | cumple | Toda la validación es del servidor (`ApiSupport.cs:8-77`); el frontend solo anticipa errores (`docs/API.md` §8). |
| 1.5.4 | Codificación de salida cerca del intérprete | cumple | React escapa el texto; System.Text.Json codifica el JSON. |
| 1.6.1 | Política explícita de gestión de claves y su ciclo de vida | parcial | Sección «Inventario de claves y secretos» y `docs/RUNBOOK.md` §7. No hay una política formal alineada con NIST SP 800-57. |
| 1.6.2 | Material de claves en bóveda o API equivalente | parcial | Archivos de secretos de solo lectura con permisos mínimos; `compose.yaml:5` aclara que no es una bóveda. |
| 1.6.3 | Claves y contraseñas reemplazables, con proceso de recifrado | parcial | Hay rotación con `infra/scripts/rotate-secrets.sh` y rotación automática del key ring. Lo cifrado con claves viejas no se vuelve a cifrar: las claves viejas se conservan. `alvarez_admin` no tiene script. |
| 1.6.4 | Secretos del lado del cliente tratados como inseguros | cumple | El bundle no tiene secretos ni `VITE_*`; el CI lo verifica (`.github/workflows/ci.yml:42-44`). |
| 1.7.1 | Formato de log común | parcial | API en JSON en producción (`API/Program.cs:55-57`); auditoría en tabla. Los dos Nginx usan formatos distintos (`NGX/finanzas.conf:9` y el formato por defecto de la imagen). |
| 1.7.2 | Logs enviados a un sistema remoto | pendiente | Por decisión, no hay envío externo (`AGENTS.md`: sin monitorización externa por defecto). Queda a elección del propietario. |
| 1.8.1 | Datos sensibles identificados y clasificados | cumple | Sección «Clasificación de datos». |
| 1.8.2 | Requisitos de protección por nivel, aplicados | parcial | Aplicados, salvo el cifrado en reposo de los datos financieros (R5). |
| 1.9.1 | Comunicación cifrada entre componentes | parcial | TLS del cliente al Nginx del host (`NGX/finanzas.conf:25-41`). Dentro del host, loopback, `web`→`api` y `api`→`db` van en claro, en redes Docker de un solo host (`compose.yaml:64,119-126`). |
| 1.9.2 | Autenticidad de cada extremo (validación TLS) | parcial | certbot y restic validan TLS. Los enlaces internos no usan TLS (ver 1.9.1). |
| 1.10.1 | Control de versiones con trazabilidad | parcial | Git y GitHub, CI en cada push (`ci.yml:3-5`). No hay un procedimiento de tickets por cambio ni protección de rama comprobable desde el repo. |
| 1.11.1 | Componentes documentados por su función de negocio o seguridad | cumple | Sección «Componentes y fronteras de confianza»; `docs/API.md`. |
| 1.11.2 | Flujos de alto valor sin estado compartido no sincronizado | cumple | Sesiones, consumo de TOTP y códigos, e idempotencia de pagos e importaciones se resuelven en la base con operaciones atómicas (`Security.cs:59-67,98-108`; `AppDbContext.cs:128,174`). Excepción: los limitadores están en memoria; valen para una sola instancia (R4). |
| 1.12.2 | Archivos subidos servidos como descarga o desde otro dominio | no aplica | Los archivos importados no se guardan ni se sirven: solo se guarda su hash (`ImportExportEndpoints.cs:107-118`). |
| 1.14.1 | Segregación por nivel de confianza (red, proxy) | cumple | Base en una red `internal`; API sin puertos publicados; `web` solo en loopback (`compose.yaml:91-92,119-126`). El escaneo externo en la VPS está pendiente (`docs/RUNBOOK.md:91`). |
| 1.14.2 | Despliegue con firmas o conexiones verificadas | parcial | Clonado por HTTPS con commit fijo (`COMMIT=<sha>`, `docs/RUNBOOK.md:31`); lockfiles. Las imágenes base se fijan por tag, no por digest (`infra/docker/*.Dockerfile`), salvo certbot. |
| 1.14.3 | El pipeline avisa de componentes desactualizados o inseguros | parcial | `dotnet list --vulnerable` y `npm audit` fallan el CI (`ci.yml:23-26,45`); NuGetAudit en la compilación (`Directory.Build.props`). Las imágenes no se escanean. |
| 1.14.4 | El pipeline construye y verifica el despliegue | parcial | El CI construye las 3 imágenes y valida Compose (`ci.yml:47-54`). `preflight.sh` (TLS, `check-config`) se corre a mano, no en CI. |
| 1.14.5 | Aislamiento en contenedores y a nivel de red | cumple | `read_only`, `cap_drop: ALL`, `no-new-privileges`, tmpfs y redes separadas (`compose.yaml:8-14,44-45,60-61,89-90`). |
| 1.14.6 | Sin tecnologías de cliente obsoletas (Flash y similares) | cumple | SPA React sin plugins; CSP `object-src 'none'` (`NGX/web/security-headers.conf:3`). |

## V2 Autenticación

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 2.1.1 | Mínimo de 12 caracteres, tras combinar espacios | parcial | El mínimo es **15** sin reglas de composición (`API/Program.cs:117-122`). Pero el largo se cuenta en unidades UTF-16 y no se combinan los espacios: Identity acepta `"a"` + 13 espacios + `"b"` y 8 emojis (16 unidades) [B-4]. |
| 2.1.2 | Se permiten al menos 64 caracteres y se rechazan más de 128 | cumple | Máximo 128 (`AuthEndpoints.cs:16-17`): en el login devuelve 401 genérico (`:122-125`) [B-14]; en el cambio, 400 (`:373-375`); en la CLI, error (`Cli.cs:314-318`). Una contraseña de 64 emojis (128 unidades) entra. |
| 2.1.3 | Sin truncado de contraseñas | parcial | El servidor no trunca ni recorta (`AuthEndpoints.cs:121`). El frontend usa `maxLength={128}` (`WEB/auth/AuthPages.tsx:68`, `WEB/pages/SettingsPage.tsx:393-396`, `WEB/components/Ui.tsx:157`): una contraseña pegada de más de 128 caracteres se corta **sin aviso**, y el control de `SettingsPage.tsx:354` nunca llega a dispararse. |
| 2.1.4 | Cualquier carácter Unicode imprimible | cumple | Identity no restringe caracteres; se aceptan acentos, ñ, espacios y emojis [B-4]. La CLI acepta cualquier carácter que no sea de control (`Cli.cs:330`). |
| 2.1.5 | El usuario puede cambiar su contraseña | cumple | `POST /auth/password/change` (`AuthEndpoints.cs:368-393`); IT `Changing_password_revokes_all_sessions`. |
| 2.1.6 | El cambio exige la contraseña actual y la nueva | cumple | `ChangePasswordAsync(user, current, new)` (`AuthEndpoints.cs:380`), además de la elevación con contraseña y TOTP (`:50`). |
| 2.1.7 | Comparar con contraseñas filtradas | pendiente | No hay lista local ni consulta k-anónima. |
| 2.1.8 | Medidor de fortaleza | pendiente | No existe en `WEB/pages/SettingsPage.tsx` ni en la CLI. |
| 2.1.9 | Sin reglas de composición | cumple | `RequireDigit`, `RequireLowercase`, `RequireUppercase` y `RequireNonAlphanumeric` en `false` (`API/Program.cs:118-122`); se aceptan 15 dígitos [B-4]. |
| 2.1.10 | Sin rotación periódica ni historial obligatorio | cumple | No existe esa función. |
| 2.1.11 | Se permite pegar y usar gestores de contraseñas | cumple | `autoComplete="current-password"` y `"new-password"` (`WEB/auth/AuthPages.tsx:64,68`; `WEB/pages/SettingsPage.tsx:393-396`); ningún `onPaste` bloquea. Ver 2.1.3 sobre `maxLength`. |
| 2.1.12 | Ver temporalmente la contraseña enmascarada | pendiente | No hay control de «mostrar contraseña»; depende del navegador. |
| 2.2.1 | Controles contra automatización; ≤ 100 fallos por hora por cuenta | cumple | Límites de 20/min por IP antes de PBKDF2 y 5/min por cuenta inexistente o no (`AuthEndpoints.cs:115-127`); bloqueo de 10 fallos por 15 min (`API/Program.cs:123-125`); `limit_req` en Nginx (`NGX/finanzas.conf:10,55-60`). Como máximo ~10 verificaciones reales cada ~17 min, unas 35 por hora. IT `Account_rate_limit_cannot_be_bypassed_by_changing_forwarded_ip`, `Lockout_after_ten_failures_blocks_even_correct_password`; el límite por IP se observó en [B-18]. El bloqueo se puede usar contra el dueño: **R1**. |
| 2.2.2 | Autenticadores débiles (SMS, email) solo como secundarios | cumple | No se usan SMS ni email: el segundo factor es TOTP. |
| 2.2.3 | Notificación segura tras cambios en la autenticación | pendiente | v1 no tiene canal de notificación (sin email, por `docs/03-SEGURIDAD.md:17`). Solo se audita. |
| 2.3.1 | Contraseñas o códigos iniciales aleatorios y con caducidad | no aplica | No hay contraseña inicial generada: el propietario elige la suya en la CLI (`Cli.cs:139-161`). |
| 2.3.2 | Autenticadores de usuario tipo U2F o FIDO | pendiente | Sin WebAuthn ni passkeys: quedan fuera de alcance en v1 (`docs/03-SEGURIDAD.md:6`). |
| 2.3.3 | Aviso para renovar autenticadores con vencimiento | no aplica | TOTP no vence. |
| 2.4.1 | Contraseñas con KDF y sal, resistentes a ataques offline | cumple | Identity v3, PBKDF2-HMAC-SHA512 (`API/Program.cs:130-134`) [B-1]; anexo A. |
| 2.4.2 | Sal de al menos 32 bits, única por credencial | cumple | Sal aleatoria de **128 bits** por hash; dos hashes de la misma contraseña tienen sal distinta [B-1]. |
| 2.4.3 | PBKDF2 con al menos 100.000 iteraciones | cumple | **210.000** iteraciones (`API/Program.cs:21-22`), lo recomendado por OWASP para HMAC-SHA512. Una política anterior dispara el rehash al ingresar [B-3]. |
| 2.4.4 | bcrypt con factor ≥ 10 | no aplica | No se usa bcrypt. |
| 2.4.5 | Iteración adicional con sal secreta («pepper») | pendiente | No implementado. |
| 2.5.1 | Secreto de recuperación no enviado en claro | cumple | Los códigos se muestran una vez, por HTTPS, al usuario ya autenticado (`AuthEndpoints.cs:261,268,398-400`); no se envían por otro canal. |
| 2.5.2 | Sin pistas ni preguntas secretas | cumple | No existen. |
| 2.5.3 | La recuperación no revela la contraseña actual | cumple | Solo se guarda el hash; `recover-owner` define una nueva (`Cli.cs:164-196`). |
| 2.5.4 | Sin cuentas compartidas ni por defecto | cumple | No hay credenciales por defecto. `bootstrap-owner` se niega si ya hay un usuario (`Cli.cs:141-145`). Los roles de la base tienen contraseñas aleatorias (`bootstrap-secrets.sh:20-23`). |
| 2.5.5 | Avisar cuando cambia un factor | pendiente | Sin canal de notificación (ver 2.2.3). |
| 2.5.6 | Recuperación con un mecanismo seguro | cumple | Códigos de recuperación de un uso, o CLI por SSH (`Cli.cs:164-196`); no hay endpoint público de recuperación (`AuthEndpoints.cs:28-54`). |
| 2.5.7 | Si se pierde el factor, verificar identidad al nivel del alta | cumple | Hay dos vías: el código de recuperación (contraseña + secreto en poder del usuario), o el operador por SSH. En ambos casos se revocan todas las sesiones (`AuthEndpoints.cs:196-200`, `Cli.cs:189-191`). |
| 2.6.1 | Secretos de consulta de un solo uso | cumple | `UPDATE` condicional sobre `UsedAt == null` (`Security.cs:98-108`); IT `Totp_code_cannot_be_reused_and_recovery_codes_are_single_use`. |
| 2.6.2 | 112 bits, o sal única de 32 bits y hash aprobado | parcial | 16 caracteres de un alfabeto de 32 = **80 bits** (`Security.cs:73-74,88-90`), con SHA-256 y un prefijo fijo, sin sal individual (`:79`). Lo mitigan el límite de 5/min y el bloqueo. |
| 2.6.3 | Resistentes a ataques offline o predicción | cumple | `RandomNumberGenerator.GetInt32` (`Security.cs:89`). |
| 2.7.1–2.7.6 | Verificadores fuera de banda (SMS, push) | no aplica (6) | No hay autenticación fuera de banda. |
| 2.8.1 | TOTP con vida definida | cumple | Paso de 30 s con ventana de ±1 (`Security.cs:44,52`). |
| 2.8.2 | Claves simétricas de OTP muy protegidas (HSM o almacén del SO) | parcial | El secreto va cifrado con Data Protection y el key ring con un certificado (`API/Program.cs:80-113`) [B-6]. No hay HSM ni almacén del SO: la clave queda en memoria del proceso y el certificado en un archivo con permisos. |
| 2.8.3 | Algoritmos aprobados en generación y verificación de OTP | cumple | RFC 6238 con Otp.NET. El secreto de 160 bits sale de `RandomNumberGenerator` [B-5]; el HMAC es SHA-1, el algoritmo por defecto de RFC 6238 y el que admiten todas las apps de autenticación. |
| 2.8.4 | TOTP usable una sola vez en su ventana | cumple | `UPDATE … WHERE totp_last_time_step < step` atómico (`Security.cs:59-67`); IT `Totp_code_cannot_be_reused_and_recovery_codes_are_single_use`. |
| 2.8.5 | Reutilización registrada, rechazada y notificada | parcial | Se rechaza y se audita como `auth.mfa` con `failure` (`AuthEndpoints.cs:188-192`), sin distinguir la reutilización y sin notificar. |
| 2.8.6 | Revocar un generador OTP físico | no aplica | No hay tokens físicos. Para el TOTP de software existe `POST /auth/mfa/reset`, que revoca todas las sesiones (`AuthEndpoints.cs:403-420`). |
| 2.8.7 | Biometría solo como factor secundario | no aplica | No hay biometría. |
| 2.9.1–2.9.3 | Verificador criptográfico (tarjetas, FIDO) | no aplica (3) | No existe. |
| 2.10.1 | Secretos entre servicios sin credenciales fijas | parcial | La API usa contraseña para la base, rotable (`rotate-secrets.sh db-app`); no hay credenciales efímeras. |
| 2.10.2 | Cuentas de servicio sin credenciales por defecto | cumple | Los roles `alvarez_app`, `alvarez_migrator` y `alvarez_admin` usan contraseñas aleatorias (`01-roles.sh`, `bootstrap-secrets.sh:16`). |
| 2.10.3 | Contraseñas de servicio resistentes a recuperación local | parcial | Archivos 0400/0440 de solo lectura (`bootstrap-secrets.sh:39-46`), no cifrados: root las lee. |
| 2.10.4 | Secretos fuera del código y del repositorio | cumple | `.gitignore` cubre secretos y claves; gitleaks revisa todo el historial en CI (`ci.yml:56-63`); se leen de archivos `*_FILE` (`API/Infrastructure/AppOptions.cs:34-59`). Única excepción: la contraseña de desarrollo local marcada como no secreta (`appsettings.Development.json`). |

## V3 Gestión de sesiones

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 3.1.1 | Tokens de sesión nunca en la URL | cumple | Solo cookies `HttpOnly` (`DbTicketStore.cs:193-200`); el token CSRF va en un header, desde memoria (`WEB/api/client.ts:41-59,81`). |
| 3.2.1 | Token nuevo al autenticarse | cumple | El login cierra las sesiones previas y emite otra preauth; MFA y el alta emiten una sesión nueva; la reautenticación rota la referencia (`AuthEndpoints.cs:152-156,201-203,263-266,355-363`; `DbTicketStore.cs:216-231`). No hay prueba automatizada que rechace la cookie anterior a la rotación. |
| 3.2.2 | Al menos 64 bits de entropía | cumple | 256 bits de `RandomNumberGenerator.GetBytes(32)` (`DbTicketStore.cs:83`). |
| 3.2.3 | Token guardado solo de forma segura en el navegador | cumple | Cookie `__Host-` `HttpOnly`; sin `localStorage` ni `sessionStorage` en `WEB/`; E2E (`flows.spec.ts:381`) verifica el almacenamiento vacío. |
| 3.2.4 | Generado con algoritmos aprobados | cumple | CSPRNG del sistema; en la base solo el SHA-256 (`DbTicketStore.cs:55,87`). |
| 3.3.1 | Logout y expiración invalidan el token | cumple | La revocación queda en la base y se comprueba en cada request (`DbTicketStore.cs:116-120,162-173`); IT `Logout_and_revocation_take_effect_on_next_request` (una cookie copiada da 401 después del logout). |
| 3.3.2 | Reautenticación periódica | cumple | 30 min de inactividad y 12 h absolutas; la renovación no extiende el máximo (`DbTicketStore.cs:64-73,118-119`); IT `Idle_timeout_and_absolute_maximum_are_enforced`. |
| 3.3.3 | Terminar las demás sesiones tras cambiar la contraseña | cumple | Se revocan todas, incluida la actual (`AuthEndpoints.cs:388-390`); también con recuperación (`:196-200`) y `recover-owner` (`Cli.cs:189-191`). IT `Changing_password_revokes_all_sessions`. |
| 3.3.4 | Ver sesiones y cerrarlas tras reingresar credenciales | parcial | Hay lista, revocación individual y «cerrar todas» (`AuthEndpoints.cs:280-330`); IT `Logout_and_revocation_take_effect_on_next_request`. No se pide reautenticación para cerrar sesiones. `logout-all` no tiene prueba automatizada. |
| 3.4.1 | Cookie con `Secure` | cumple | `SecurePolicy = Always` (`DbTicketStore.cs:195`, `API/Program.cs:172`); observado en la cookie CSRF [B-11]. |
| 3.4.2 | Cookie con `HttpOnly` | cumple | `DbTicketStore.cs:194`; [B-11]. |
| 3.4.3 | Cookie con `SameSite` | cumple | `Lax` en sesión y preauth (`DbTicketStore.cs:196`); `Strict` en la cookie CSRF (`API/Program.cs:173`) [B-11]. |
| 3.4.4 | Prefijo `__Host-` | cumple | `__Host-alvarez-session`, `__Host-alvarez-preauth` y `__Host-alvarez-csrf` (`DbTicketStore.cs:18-20`), con `Path=/` y sin `Domain` (`:197-198`). |
| 3.4.5 | Path preciso si se comparte el dominio con otras apps | parcial | `__Host-` exige `Path=/`; correcto con un nombre propio dedicado. En modo IP o con el nombre del proveedor, otros servicios HTTPS del mismo host reciben la cookie: las cookies no se aíslan por puerto. El instalador lo advierte (`docs/RUNBOOK.md:46`). |
| 3.5.1 | Revocar tokens OAuth | no aplica | No hay OAuth. |
| 3.5.2 | Tokens de sesión en vez de claves estáticas | cumple | Sesiones de servidor; no hay claves de API. |
| 3.5.3 | Tokens sin estado firmados o cifrados | no aplica | Las sesiones tienen estado en el servidor; el navegador no recibe JWT. |
| 3.7.1 | Sesión completa o reautenticación antes de operaciones sensibles | cumple | La política `Elevated` (contraseña + TOTP, 5 min) protege el cambio de contraseña, la regeneración de códigos, el reinicio de MFA y la exportación (`AuthEndpoints.cs:50-52`, `ImportExportEndpoints.cs:28`); IT `Export_requires_recent_reauthentication_and_is_not_cacheable`. Los otros tres endpoints elevados no tienen prueba negativa. |

## V4 Control de acceso

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 4.1.1 | Control de acceso en la capa confiable | cumple | Políticas por defecto, de reserva, `Financial` y `Elevated` (`API/Program.cs:144-166,249`); IT `Private_endpoints_without_session_return_401_json_not_html`. |
| 4.1.2 | Atributos de control no manipulables por el usuario | cumple | El propietario sale de los claims (`Security.cs:178-179`); `UnmappedMemberHandling.Disallow` (`API/Program.cs:210`); IT `Unknown_json_properties_are_rejected_no_mass_assignment`. |
| 4.1.3 | Mínimo privilegio | cumple | `AllowAnonymous` solo en health, `csrf`, `me`, `login`, `logout` y la ruta comodín 404 (`API/Program.cs:243-260`, `AuthEndpoints.cs:38-45`); la preauth no accede a datos. IT `Login_errors_are_generic_and_password_alone_gives_no_financial_access`. |
| 4.1.5 | El control de acceso falla de forma segura | cumple | La política de reserva exige sesión con MFA; un ticket ilegible da `null` (`DbTicketStore.cs:122-131`); `UserId()` lanza excepción si falta el claim (`Security.cs:178-179`), lo que termina en 500 sin datos [B-16]. |
| 4.2.1 | Protección contra IDOR al crear, leer, actualizar y borrar | cumple | Filtros `OwnerId` en todas las consultas (por ejemplo, `EntryEndpoints.cs:47,83,130`; `CashEndpoints.cs:126,191-202`); FK compuestas `(owner_id, id)` (`AppDbContext.cs:105-110,231-237`). IT `User_b_cannot_read_modify_relate_or_aggregate_user_a_records`, `Database_rejects_cross_owner_references_even_bypassing_the_api`, `Commit_requires_confirmation_and_owner`. |
| 4.2.2 | Anti-CSRF fuerte; anti-automatización en lo no autenticado | cumple | Antiforgery del framework en todo método inseguro bajo `/api`, login incluido, más el control de `Origin`/`Referer` (`Middleware.cs:43-74`); IT `Csrf_missing_foreign_token_or_foreign_origin_is_rejected`; [B-10]. |
| 4.3.1 | MFA en interfaces administrativas | no aplica | No hay interfaz administrativa web: la administración es por CLI y SSH con claves (`docs/RUNBOOK.md:9`). |
| 4.3.2 | Sin listado de directorios ni metadatos (`.git` y similares) | cumple | La imagen `web` solo copia `dist` (`infra/docker/web.Dockerfile:18`); Nginx sin `autoindex`; `try_files` (`NGX/web/default.conf:69-86`); sin sourcemaps (`src/web/vite.config.ts:18`). |
| 4.3.3 | Autenticación adicional (step-up) para lo sensible | cumple | Ver 3.7.1. |

## V5 Validación, sanitización y codificación

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 5.1.1 | Defensa contra contaminación de parámetros HTTP | parcial | El binding separa ruta, query, header y cuerpo. Según el binding de ASP.NET Core, una query repetida se une con comas y luego falla la validación estricta (mes, moneda, GUID); esto no se probó acá. Una clave JSON duplicada se acepta, sin rechazo explícito [B-15]. |
| 5.1.2 | Protección contra asignación masiva | cumple | DTO sin `ownerId` ni roles y propiedades desconocidas → 400 (`API/Program.cs:210`) [B-12]; IT `Unknown_json_properties_are_rejected_no_mass_assignment`. |
| 5.1.3 | Validación positiva (allowlist) de toda entrada | parcial | Enums por mapas, moneda, fechas, importes, `Last4` e `Idempotency-Key` con allowlist (`ApiSupport.cs:42-76`, `CashEndpoints.cs:109-111`). En textos libres: largo máximo y rechazo de caracteres de control. Brecha menor: un `status` desconocido en `GET /plans` se trata como «todos», sin rechazarlo (`PlanEndpoints.cs:142`). |
| 5.1.4 | Datos estructurados tipados y validados contra esquema | cumple | DTO tipados con `NumberHandling.Strict` (`API/Program.cs:211`); reglas cruzadas (moneda de la cuenta, ingreso en tarjeta, desembolso solo en préstamo, cuotas ≤ centavos) (`EntryEndpoints.cs:100-102`, `PlanEndpoints.cs:85-108,182-200`); esquema documentado en `docs/API.md`. |
| 5.1.5 | Redirecciones solo a destinos permitidos | cumple | La API no redirige: el inicio de sesión responde 401 y no hay `returnUrl` (`DbTicketStore.cs:204-207`). Nginx redirige solo de `http` a `https` en el mismo host (`NGX/finanzas.conf:22`). |
| 5.2.1 | Sanitizar HTML de editores WYSIWYG | no aplica | No se acepta HTML; todo es texto plano. |
| 5.2.2 | Datos no estructurados con caracteres y largo acotados | cumple | `RequiredText` y `OptionalText`: recorte, largo máximo y sin caracteres de control (`ApiSupport.cs:24-40`); límites también en la base (`AppDbContext.cs:42,66-67`). |
| 5.2.3 | Sanitizar entradas a sistemas de correo | no aplica | No hay correo. |
| 5.2.4 | Sin `eval` ni ejecución dinámica | cumple | No hay `eval` en `WEB/`; la CSP no incluye `unsafe-eval` (`NGX/web/security-headers.conf:3`). |
| 5.2.5 | Inyección de plantillas | no aplica | No hay plantillas de servidor: la UI es React. |
| 5.2.6 | SSRF | no aplica | La API no hace requests salientes con datos del usuario. |
| 5.2.7 | SVG subido por el usuario | no aplica | No se suben SVG. El gráfico es SVG que genera React con texto escapado. |
| 5.2.8 | Contenido con plantillas o expresiones (Markdown, CSS) | no aplica | No se acepta ese contenido. |
| 5.3.1 | Codificación de salida según el contexto | cumple | React escapa; System.Text.Json; `Content-Disposition` con nombre fijo (`ImportExportEndpoints.cs:511`). |
| 5.3.2 | Preservar el juego de caracteres y Unicode | cumple | UTF-8 de punta a punta; IT `Legacy_import_preview_commit_is_idempotent_and_detects_conflicts` conserva `<b>Súper</b>`. |
| 5.3.3 | Escape contra XSS reflejado, almacenado y de DOM | cumple | No hay `dangerouslySetInnerHTML` ni `innerHTML` en `WEB/`; IT `Html_in_concept_is_stored_and_returned_as_plain_text`; E2E «XSS en concepto…» (`flows.spec.ts:256-265`) y «sin violaciones de CSP» (`:384-387`). |
| 5.3.4 | Consultas parametrizadas u ORM | cumple | EF Core LINQ; `ILIKE` con parámetro y escape de `%` y `_` (`EntryEndpoints.cs:60-67,77`); el SQL crudo de las pruebas usa `ExecuteSqlInterpolated`, que está parametrizado. |
| 5.3.5 | Escape específico donde no hay parametrización | cumple | El único SQL armado interpola un nombre de rol validado con `^[a-z_][a-z0-9_]{0,62}$`, porque los identificadores no se pueden parametrizar (`Cli.cs:24-25,74,82-90`). |
| 5.3.6 | Inyección JSON o JavaScript | cumple | Serializador del framework; `JSON.parse` en el cliente (`WEB/api/client.ts:115-121`). |
| 5.3.7 | Inyección LDAP | no aplica | No hay LDAP. |
| 5.3.8 | Inyección de comandos del SO | no aplica | La aplicación no ejecuta comandos del sistema. |
| 5.3.9 | Inclusión de archivos locales o remotos | no aplica | No hay rutas de archivo tomadas del usuario. |
| 5.3.10 | Inyección XPath o XML | no aplica | No se procesa XML del usuario. |
| 5.4.1 | Manejo de memoria seguro | cumple | Código administrado (.NET, TypeScript); sin `unsafe`. |
| 5.4.2 | Cadenas de formato constantes | cumple | Logs con plantillas constantes (`Audit.cs:28`, `Middleware.cs:68,120`). |
| 5.4.3 | Prevención de desbordes enteros | cumple | Aritmética `checked`; importes ≤ 1e14 y ≤ 18 dígitos (`API/Domain/Money.cs:31-42,80-100`); checks en la base (`AppDbContext.cs:75-91`). UT `TryParseMinor_accepts_only_positive_integer_strings`, `ParseDecimalText_rejects_without_truncating`, `Distribute_rejects_more_installments_than_cents`. |
| 5.5.1 | Objetos serializados con integridad o cifrado | cumple | El ticket de sesión va protegido con Data Protection (cifrado autenticado) (`DbTicketStore.cs:90,125`); la vista previa de importación queda en el servidor. |
| 5.5.2 | Parsers XML restrictivos (XXE) | no aplica | No se parsea XML no confiable. El key ring es XML local de la aplicación. |
| 5.5.3 | Deserialización de datos no confiables protegida | cumple | DTO tipados sin polimorfismo; la importación usa `JsonDocument` con `MaxDepth` 8, claves permitidas y límites (`API/Application/ImportFormats.cs:32-91`). UT `Rejects_invalid_files`, `Rejects_duplicate_ids_and_excess_depth_and_record_limit`. |
| 5.5.4 | `JSON.parse` en el navegador, no `eval` | cumple | `WEB/api/client.ts:115-121`. |

## V6 Criptografía almacenada

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 6.1.1 | Datos personales regulados cifrados en reposo | parcial | Backups cifrados con restic (`backup-entrypoint.sh:25-42`) y secretos MFA cifrados. La base va en claro y el cifrado del volumen depende del proveedor (R5). |
| 6.1.2 | Datos de salud cifrados | no aplica | No hay datos de salud. |
| 6.1.3 | Datos financieros regulados cifrados en reposo | parcial | Igual que 6.1.1. |
| 6.2.1 | Los módulos criptográficos fallan de forma segura, sin padding oracle | cumple | Data Protection usa cifrado autenticado (AES-256-CBC + HMAC-SHA256, encrypt-then-MAC) [B-6]. Un fallo devuelve `null` o 401 sin detalle (`DbTicketStore.cs:127-130`). |
| 6.2.2 | Algoritmos y bibliotecas probados, sin cripto propia | cumple | Data Protection, Identity `PasswordHasher`, `RandomNumberGenerator`, HMAC-SHA256, SHA-256, Otp.NET y restic. No hay implementaciones propias (`AGENTS.md`). |
| 6.2.3 | IV y modos configurados según la recomendación vigente | cumple | Valores por defecto de Data Protection: IV aleatorio por operación, AES-256-CBC + HMAC-SHA256 [B-6]. |
| 6.2.4 | Algoritmos reemplazables (agilidad) | cumple | Data Protection admite cambiar algoritmos y rota claves cada 90 días; `PasswordHasher` hace rehash al ingresar si cambia la política [B-3]. |
| 6.2.5 | Sin modos, rellenos ni hashes débiles salvo por compatibilidad | parcial | El framework envuelve las claves del key ring con **RSA PKCS#1 v1.5** (`xmlenc#rsa-1_5`) y AES-256-CBC [B-6]. Se descifra solo localmente, al arrancar, así que no hay oráculo expuesto, pero es un relleno que ASVS desaconseja. TOTP usa HMAC-SHA1 por compatibilidad con RFC 6238. |
| 6.2.6 | Nonces e IV no reutilizados | cumple | Data Protection genera un IV aleatorio por operación. |
| 6.3.1 | Valores aleatorios no adivinables con CSPRNG | cumple | `RandomNumberGenerator` para sesiones, códigos y la clave HMAC (`DbTicketStore.cs:83`, `Security.cs:89,124`); `crypto.randomUUID()` en el cliente (`WEB/api/client.ts:148-150`); Otp.NET [B-5]. |
| 6.3.2 | GUID aleatorios v4 con CSPRNG | no aplica | Los IDs son UUID v7 a propósito, para que los índices queden ordenados (`API/Domain/Entities.cs:8`). No son secretos: el acceso se decide por propietario (`docs/03-SEGURIDAD.md:32`). |
| 6.4.1 | Gestión de secretos en una bóveda | parcial | Archivos montados de solo lectura (`compose.yaml:5,74-77,132-136`); no hay bóveda. |
| 6.4.2 | Material de claves fuera de la aplicación (módulo aislado) | pendiente | Las claves de Data Protection y el certificado se cargan en el proceso de la API (`API/Program.cs:80-113`). No hay HSM ni KMS. |

## V7 Manejo de errores y registro

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 7.1.1 | Sin credenciales ni datos de pago en los logs; sesiones solo con hash | cumple | No hay `HttpLogging` ni logging de cuerpos; `Microsoft.AspNetCore` en `Warning` (`appsettings.json`); la auditoría no guarda importes ni conceptos (`Audit.cs:7-29`); en la base solo el hash de la sesión. No hay escaneo automatizado de logs. |
| 7.1.2 | Sin otros datos sensibles en los logs | parcial | El Nginx del contenedor `web` no define `access_log` (`NGX/web/default.conf`), así que hereda el formato `main` de la imagen, que registra `"$request"`. Eso incluye búsquedas (`/api/v1/entries?q=…`, `/movements?…&q=…`), la IP completa y el User-Agent (verificado leyendo `/etc/nginx/nginx.conf` de la imagen: `log_format main … "$request"`). El Nginx del host registra la ruta sin query, pero con la IP completa (`NGX/finanzas.conf:9`). |
| 7.1.3 | Registrar eventos de seguridad: autenticación, control de acceso, validación | parcial | Se auditan éxito y fallo de login, MFA, alta, reautenticación, logout, recuperación, cambios de contraseña y MFA, y revocaciones (`AuthEndpoints.cs:134-435`), además de la importación inválida (`ImportExportEndpoints.cs:60`). El rechazo CSRF va al log (`Middleware.cs:68`). No se registran los 401/403/404 de autorización, los 429 ni los errores de validación. |
| 7.1.4 | Cada evento con lo necesario para reconstruir la línea de tiempo | cumple | `audit_events`: actor, acción, recurso, resultado, `OccurredAt` en UTC, correlation id e IP minimizada (`Audit.cs:13-29`). |
| 7.2.1 | Todas las decisiones de autenticación registradas, sin tokens | parcial | Ver 7.1.3. No se registran el rechazo por límite (429) ni las sesiones rechazadas por vencidas o revocadas (`DbTicketStore.cs:116-132`). |
| 7.2.2 | Decisiones de acceso registrables; las fallidas, registradas | parcial | Los fallos de política no se persisten. Un ID ajeno devuelve 404 sin registro. |
| 7.3.1 | Codificación contra inyección en logs | cumple | Logging estructurado con plantillas (JSON en producción, `API/Program.cs:56`); la auditoría va en columnas tipadas. |
| 7.3.3 | Logs de seguridad protegidos contra acceso y modificación | parcial | El rol runtime no tiene `UPDATE`, `DELETE` ni `TRUNCATE` sobre `audit_events` (`Cli.cs:86`). El migrador, que es el dueño de la tabla, el superusuario `alvarez_admin` y root del host **sí** pueden alterarla (R2). El `REVOKE` no tiene prueba automatizada: las pruebas de integración corren como administrador. |
| 7.3.4 | Hora sincronizada; registrar en UTC | parcial | `TZ=UTC` (`infra/docker/api.Dockerfile:29`) y auditoría en UTC. Ningún script verifica la sincronización NTP del host (SEC-09). |
| 7.4.1 | Mensaje genérico ante errores, con ID de soporte | cumple | ProblemDetails sin `exception`, con `correlationId` (`API/Program.cs:68-72`). El 500 observado no trae stack, SQL ni detalle [B-16]. |
| 7.4.2 | Manejo de excepciones en todo el código | cumple | Capturas específicas de concurrencia, unicidad, serialización y criptografía (`CatalogEndpoints.cs:240-277`, `CashEndpoints.cs:219-234`, `DbTicketStore.cs:127`). |
| 7.4.3 | Manejador de último recurso | cumple | `UseExceptionHandler` y `UseStatusCodePages` (`API/Program.cs:233-234`); la CLI tiene su propio catch-all (`Cli.cs:40-44`). |

## V8 Protección de datos

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 8.1.1 | Sin caché de datos sensibles en componentes del servidor | cumple | `Cache-Control: no-store` en toda respuesta de la API (`Middleware.cs:26`) [B-7]; Nginx sin `proxy_cache`. IT `Private_endpoints_without_session_return_401_json_not_html`, `Export_requires_recent_reauthentication_and_is_not_cacheable`. |
| 8.1.2 | Copias temporales protegidas o purgadas | cumple | La vista previa caduca y se purga (`Middleware.cs:108-110`, `ImportExportEndpoints.cs:193`); los tickets revocados o vencidos se anulan y se borran a los 30 días (`Middleware.cs:111-116`). El archivo subido no se guarda. Nota: el MVCC y los backups de PostgreSQL pueden conservar una vista previa vigente hasta su retención. |
| 8.1.3 | Pocos parámetros por request | cumple | DTO mínimos; una cookie de sesión y un header CSRF. |
| 8.1.4 | Detectar y alertar volúmenes anómalos de requests | parcial | Hay límites por IP, cuenta y usuario (sección 4 de `docs/API.md`), pero no detección con alertas. |
| 8.2.1 | Cabeceras anti-caché en el navegador | cumple | `no-store` y `Pragma: no-cache` en la API [B-7]; `fetch` con `cache: 'no-store'` (`WEB/api/client.ts:90`); `index.html` con `no-cache` (`NGX/web/default.conf:84`). |
| 8.2.2 | Sin datos sensibles en el almacenamiento del navegador | cumple | No hay `localStorage`, `sessionStorage`, IndexedDB ni service worker en `WEB/`; la caché de consultas vive solo en memoria (`WEB/main.tsx:24-25`). E2E `flows.spec.ts:381`. |
| 8.2.3 | Datos autenticados borrados del cliente al cerrar la sesión | cumple | `signOutLocally` borra las consultas al hacer logout y ante un 401 (`WEB/pages/queries.ts:18-21`, `WEB/App.tsx:24-26`, `WEB/layout/Shell.tsx:38-39`). E2E «logout limpia la sesión…» (`flows.spec.ts:375-382`). |
| 8.3.1 | Datos sensibles en cuerpo o headers, no en la query | parcial | Contraseñas, códigos y montos de escritura van en el cuerpo. Las búsquedas (`q`) y los filtros de lectura van en la query (`EntryEndpoints.cs:42-67`, `ReportEndpoints.cs:95-117`) y el Nginx interno los registra (ver 7.1.2). |
| 8.3.2 | El usuario puede exportar o eliminar sus datos | parcial | Exportación completa (`POST /exports`) y eliminación por registro. No hay borrado de cuenta ni de todos los datos desde la aplicación. |
| 8.3.3 | Lenguaje claro y consentimiento sobre los datos personales | no aplica | Es una instancia personal: la opera su único usuario, sin terceros ni analítica (ver 10.2.1). |
| 8.3.4 | Datos sensibles identificados, con política | cumple | Sección «Clasificación de datos»; `docs/03-SEGURIDAD.md:44-51`. |
| 8.3.5 | Acceso a datos sensibles auditado | parcial | Se audita la exportación (`ImportExportEndpoints.cs:507`) y todo cambio, pero no las lecturas. |
| 8.3.6 | Datos sensibles en memoria sobrescritos tras su uso | pendiente | Las contraseñas y secretos son `string` administrados; no se ponen a cero. |
| 8.3.7 | Cifrado con algoritmos aprobados, confidencialidad e integridad | cumple | Data Protection (AES-256-CBC + HMAC-SHA256) para TOTP y tickets; restic (AES-256-CTR + Poly1305) para backups. |
| 8.3.8 | Retención con borrado automático | parcial | Auditoría 90 días (`Cli.cs:96-105`, timer diario); sesiones a los 30 días; vistas previas a los 30 min. La papelera y los datos financieros no se purgan solos, por decisión del propietario. Los logs rotan por tamaño, no por antigüedad (R3). |

## V9 Comunicaciones

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 9.1.1 | TLS en toda conexión del cliente, sin caída a texto plano | parcial | El puerto 80 redirige a `https` (`NGX/finanzas.conf:17-23`) y las cookies `__Host-` exigen HTTPS. HSTS está comentado hasta validar HTTPS (`:43-45`); el HTTPS de producción en la VPS está **pendiente** (gate de `AGENTS.md`). |
| 9.1.2 | Solo suites de cifrado fuertes, verificado con herramientas | pendiente | No se definió `ssl_ciphers` (valores por defecto de Nginx y OpenSSL; `ssl_prefer_server_ciphers off`). No se corrió testssl ni SSL Labs contra el servidor real. |
| 9.1.3 | Solo TLS 1.2 y 1.3 | parcial | `ssl_protocols TLSv1.2 TLSv1.3` (`NGX/finanzas.conf:37`). Falta verificarlo desde afuera: en un Nginx compartido, el protocolo que se negocia puede depender del servidor por defecto del puerto 443. |
| 9.2.1 | Certificados confiables en las conexiones del servidor | parcial | Let's Encrypt en el borde. Los enlaces internos no usan TLS (ver 1.9.1). |
| 9.2.2 | TLS en toda conexión entrante y saliente, base incluida | parcial | Entre `api` y `db` no hay TLS (`compose.yaml:64`, sin `SSL Mode`); el tráfico viaja por una red Docker `internal` en el mismo host. |
| 9.2.3 | Conexiones cifradas a sistemas externos autenticadas | cumple | Las únicas conexiones externas son ACME por HTTPS y restic off-site (sftp con clave de host, o https). |
| 9.2.4 | Revocación de certificados (OCSP stapling) | no aplica | Let's Encrypt dejó de ofrecer OCSP en 2025. En modo IP el certificado dura unas 160 h (`docs/RUNBOOK.md:53`). |

## V10 Código malicioso

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 10.2.1 | Sin «llamar a casa» ni recolección de datos no autorizada | cumple | No aparecen analytics, Sentry, gtag, Datadog ni AppInsights en `src/web` ni en los `.csproj`; fuentes servidas por la propia aplicación; `DOTNET_CLI_TELEMETRY_OPTOUT=1` al compilar (`api.Dockerfile:7`). |
| 10.2.2 | Sin permisos excesivos a sensores | cumple | `Permissions-Policy` niega cámara, micrófono, geolocalización, pago y USB (`NGX/web/security-headers.conf:6`). |
| 10.3.1 | Actualización automática por canal seguro y firmada | no aplica | No hay autoactualización. El despliegue es manual con `deploy.sh`. |
| 10.3.2 | Integridad; sin código de fuentes no confiables | cumple | Todo el código sale del mismo origen (`script-src 'self'`); sin CDN; lockfiles (`npm ci`, NuGet `--locked-mode`) (`infra/docker/web.Dockerfile:11`, `infra/docker/api.Dockerfile:14`). |
| 10.3.3 | Protección contra toma de subdominios | pendiente | Depende del DNS del propietario. Hay que revisar los registros al cambiar de VPS o de nombre; en modo IP no hay DNS. |

## V11 Lógica de negocio

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 11.1.1 | Flujos en orden y sin saltear pasos | cumple | La etapa de preauth (`mfa` o `enroll`) se exige en cada paso (`AuthEndpoints.cs:164,216,234`); el alta exige el secreto pendiente; la importación exige vista previa, confirmación y clave. IT `Login_errors_are_generic_and_password_alone_gives_no_financial_access`, `Preauth_expires_after_five_minutes`, `Commit_requires_confirmation_and_owner`. |
| 11.1.2 | Pasos en tiempos humanos realistas | parcial | Solo hay límites de frecuencia; no hay tiempo mínimo entre pasos. |
| 11.1.3 | Límites por usuario para acciones de negocio | cumple | Importación y exportación 5/h, hasta 360 cuotas, importes ≤ 1e14, páginas ≤ 100, ≤ 500 asignaciones, ≤ 10.000 registros por archivo (`AppOptions.cs:20-26`, `CashEndpoints.cs:119`). |
| 11.1.4 | Anti-automatización: exfiltración, subidas, DoS | cumple | Límite global de 300/min por IP (`API/Program.cs:193-204`), más los límites de autenticación y los de 5/h; tamaños de cuerpo y tiempos máximos (`API/Program.cs:59-64,205`, `NGX/web/default.conf:14-17`). Valen para una instancia (R4). |
| 11.1.5 | Límites contra riesgos de negocio identificados | cumple | No se mezclan monedas; las asignaciones no superan lo pendiente; idempotencia; concurrencia optimista; no se reescribe la historia de planes pagados (`docs/02-ARQUITECTURA-Y-DATOS.md:30-41`). IT `FinanceTests` (9 pruebas). |
| 11.1.6 | Sin TOCTOU ni carreras en operaciones sensibles | cumple | `UPDATE` condicionales para TOTP y códigos; transacciones `Serializable` e índices únicos de idempotencia en pagos e importaciones (`CashEndpoints.cs:137`, `ImportExportEndpoints.cs:179`, `AppDbContext.cs:128,174`); versión en cada registro. IT `Concurrent_edits_get_409_not_silent_overwrite`. No se probó con concurrencia real en paralelo. |
| 11.1.7 | Monitorear eventos de negocio inusuales | pendiente | No hay monitoreo. |
| 11.1.8 | Alertas configurables ante ataques automatizados | pendiente | No hay alertas: `OnFailure=` no está configurado (`docs/RUNBOOK.md:116`). |

## V12 Archivos y recursos

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 12.1.1 | No aceptar archivos grandes | cumple | Importación ≤ 5 MiB (`ImportExportEndpoints.cs:44-55`; Nginx 6 MiB, `NGX/web/default.conf:49`); el resto ≤ 64 KiB. IT `Invalid_import_writes_nothing` (6 MiB → 413). |
| 12.1.2 | Archivos comprimidos: tamaño y cantidad | no aplica | No se aceptan archivos comprimidos ni `Content-Encoding` de request. |
| 12.1.3 | Cuota de tamaño y cantidad por usuario | cumple | 5 vistas previas por hora y por usuario, cada una ≤ 5 MiB, que caducan a los 30 min y se purgan. |
| 12.2.1 | Tipo verificado por el contenido | cumple | Se parsea como JSON estricto; el `Content-Type` solo no alcanza (`ImportFormats.cs:32-59`). |
| 12.3.1 | Nombre de archivo del usuario no usado en el sistema de archivos | no aplica | No se recibe nombre de archivo: el cuerpo es JSON crudo. |
| 12.3.2 | Nombre de archivo validado contra LFI | no aplica | Ídem. |
| 12.3.3 | Nombre de archivo validado contra RFI y SSRF | no aplica | Ídem. |
| 12.3.4 | Descarga reflejada (RFD): nombre fijo | cumple | La exportación usa un nombre fijo armado por el servidor (`ImportExportEndpoints.cs:510-511`), con `nosniff`. |
| 12.3.5 | Metadatos de archivo no usados con APIs del sistema | no aplica | No hay metadatos. |
| 12.3.6 | Sin funcionalidad de fuentes no verificadas | cumple | Paquetes de nuget.org y npm con lockfile; auditoría en CI (`ci.yml:23-26,45`). |
| 12.4.1 | Archivos no confiables fuera del web root y con permisos mínimos | cumple | No se guardan como archivo: los datos normalizados van a la base. |
| 12.4.2 | Antivirus sobre archivos no confiables | no aplica | Los archivos no se guardan ni se sirven. |
| 12.5.1 | El servidor web sirve solo extensiones previstas | cumple | La raíz contiene solo el build de Vite (`infra/docker/web.Dockerfile:18`), sin sourcemaps; no hay `.bak` ni temporales. No hay una allowlist explícita por extensión. |
| 12.5.2 | Archivos subidos nunca ejecutados como HTML o JS | no aplica | No se sirven archivos subidos. |
| 12.6.1 | Allowlist de destinos salientes del servidor | parcial | La base está en una red `internal`. La API no hace requests salientes, pero está conectada a la red `frontend`, que no es `internal`, así que tiene salida a internet sin filtro de egreso (`compose.yaml:79-81,122-125`). |

## V13 API y servicios web

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 13.1.1 | Mismos codificadores y parsers en todos los componentes | cumple | Una sola cadena (Nginx → Kestrel) en UTF-8; no hay SSRF ni RFI que explotar. |
| 13.1.3 | URLs de la API sin información sensible (tokens o claves) | cumple | No hay tokens en las URLs. Sobre las búsquedas en la query, ver 8.3.1. |
| 13.1.4 | Autorización en la ruta y en el recurso | cumple | `RequireAuthorization` por grupo (`API/Program.cs:249`) y por endpoint (`AuthEndpoints.cs:42-52`, `ImportExportEndpoints.cs:28`), más el filtro por propietario y las FK compuestas. |
| 13.1.5 | `Content-Type` inesperado o ausente rechazado con 406 o 415 | parcial | La vista previa responde 415 (`ImportExportEndpoints.cs:42-43`; IT `Invalid_import_writes_nothing`). En los endpoints con cuerpo JSON, un `Content-Type` no JSON **se rechaza con 404**, porque la ruta comodín `/api/{**rest}` (`API/Program.cs:260`) gana al selector por tipo de contenido [B-13]. Se rechaza, pero con un código equivocado. |
| 13.2.1 | Métodos HTTP válidos para cada acción | cumple | Solo se mapean los métodos previstos; cualquier otro termina en 404 por la ruta comodín [B-17]. |
| 13.2.2 | Validación de esquema JSON antes de aceptar la entrada | cumple | Deserialización tipada con allowlist (propiedad desconocida o tipo incorrecto → 400) [B-12], seguida de la validación de negocio. No es un JSON Schema formal; el esquema está en `docs/API.md`. |
| 13.2.3 | Servicios REST con cookies protegidos contra CSRF | cumple | Ver 4.2.2. |
| 13.2.5 | Verificar el `Content-Type` esperado | parcial | Ver 13.1.5. |
| 13.2.6 | Headers y cuerpo confiables en tránsito | cumple | TLS entre cliente y servidor. Los saltos internos van en claro dentro del host (ver 9.2.2). |
| 13.3.1–13.3.2 | SOAP | no aplica (2) | No hay SOAP. |
| 13.4.1–13.4.2 | GraphQL | no aplica (2) | No hay GraphQL. |

## V14 Configuración

| Req. | Requisito (resumen) | Estado | Evidencia |
|---|---|---|---|
| 14.1.1 | Build y despliegue seguros y repetibles | cumple | CI (`ci.yml`), `deploy.sh`, `vps-install.sh`, lockfiles y `--locked-mode`. |
| 14.1.2 | Flags del compilador contra desbordes | no aplica | Código administrado sin `unsafe`; `TreatWarningsAsErrors` (`Directory.Build.props`). |
| 14.1.3 | Configuración del servidor endurecida | cumple | Sin header `Server` y con límites en Kestrel (`API/Program.cs:59-64`); `server_tokens off` (`NGX/web/default.conf:6`, `NGX/finanzas.conf:30`); contenedores `read_only`, sin capacidades y no root (la extracción de restauraciones usa root temporalmente, ver 1.2.1); PostgreSQL con `scram-sha-256` y checksums (`compose.yaml:25`); `DOTNET_EnableDiagnostics=0` (`api.Dockerfile:27`). |
| 14.1.4 | Redesplegable y restaurable con runbook probado | parcial | `docs/RUNBOOK.md` y los scripts de despliegue, backup y restore, ejecutados en local según `docs/IMPLEMENTATION-STATUS.md:22-49` (no repetidos en esta revisión). Falta hacerlo en la VPS. |
| 14.2.1 | Componentes actualizados, con verificador de dependencias | parcial | `dotnet list --vulnerable`, `npm audit` y NuGetAudit en CI (`ci.yml:23-26,45`; `Directory.Build.props`). Las imágenes de contenedor no se escanean y las dependencias de `tests/e2e` no se auditan en CI. |
| 14.2.2 | Sin funciones, ejemplos ni configuración innecesarios | cumple | Sin OpenAPI ni Swagger (`AlvarezFinanzas.Api.csproj:6`); `/dev/demo` solo existe en Development (`API/Api/DevEndpoints.cs:17`); la imagen no incluye `appsettings.Development.json` (`api.Dockerfile:16-17`). No hay prueba automatizada de la ausencia de `/dev/demo` en producción. |
| 14.2.3 | SRI para recursos en CDN | no aplica | No se cargan recursos externos. |
| 14.2.4 | Componentes de repositorios confiables y mantenidos | cumple | nuget.org, npmjs e imágenes oficiales (`mcr.microsoft.com`, `postgres`, `nginxinc`), con versiones fijadas (`Directory.Packages.props`, `src/web/package.json`, `docs/VERSIONES.md`). |
| 14.2.5 | SBOM | parcial | Hay lockfiles (`packages.lock.json`, `package-lock.json`) y `docs/VERSIONES.md`, pero no un SBOM CycloneDX o SPDX. |
| 14.2.6 | Bibliotecas de terceros encapsuladas o aisladas | parcial | Aislamiento por contenedor y pocas dependencias (7 en el frontend, 5 en el servidor); no hay encapsulado específico por biblioteca. |
| 14.3.2 | Modos de depuración desactivados en producción | cumple | `ASPNETCORE_ENVIRONMENT=Production`; sin página de excepciones para desarrolladores; `sourcemap: false` (`src/web/vite.config.ts:18`). |
| 14.3.3 | Headers sin versiones de componentes | cumple | Sin `Server` [B-7]; `server_tokens off`. |
| 14.4.1 | `Content-Type` con juego de caracteres seguro | parcial | La API responde `application/json; charset=utf-8` y `application/problem+json` [B-7]. Nginx sirve `text/*` sin `charset`, aunque `index.html` declara `<meta charset="utf-8">` (`src/web/index.html:4`). |
| 14.4.2 | `Content-Disposition: attachment` en respuestas de la API | pendiente | Solo la exportación lo envía. Lo mitigan `nosniff`, la CSP `default-src 'none'` y CORP `same-origin` en cada respuesta de la API [B-7]. |
| 14.4.3 | CSP presente | cumple | La CSP del SPA no tiene `unsafe-inline` ni `unsafe-eval` (`NGX/web/security-headers.conf:3`); la API envía `default-src 'none'` (`Middleware.cs:31`). Por `/api/` viajan las dos y el navegador aplica la intersección. E2E `flows.spec.ts:384-387`. |
| 14.4.4 | `nosniff` en todas las respuestas | parcial | Está en la API [B-7] y en cada `location` del Nginx interno que incluye `security-headers.conf`. Faltan en `/healthz` y `= /api` (`NGX/web/default.conf:19-23,64-66`) y en las redirecciones y el 421 del Nginx del host: son respuestas fijas, sin contenido del usuario. |
| 14.4.5 | HSTS en todas las respuestas | pendiente | Se activa solo después de validar HTTPS con un dominio propio (`NGX/finanzas.conf:43-45`, `docs/RUNBOOK.md:89`). ASVS pide `includeSubDomains`; la especificación lo prohíbe hasta evaluar los subdominios (`docs/03-SEGURIDAD.md:39`). |
| 14.4.6 | `Referrer-Policy` adecuada | cumple | `no-referrer` en Nginx, en la API y en `index.html` (`security-headers.conf:5`, `Middleware.cs:29`, `src/web/index.html:6`). |
| 14.4.7 | No embebible: `frame-ancestors` y `X-Frame-Options` | cumple | `frame-ancestors 'none'` y `X-Frame-Options: DENY` (`security-headers.conf:3,9`; `Middleware.cs:30-31`). |
| 14.5.1 | Solo los métodos HTTP usados; registrar los inválidos | parcial | Los métodos no mapeados reciben 404 [B-17], pero no se registran ni generan alertas. |
| 14.5.2 | `Origin` no usado para autenticar ni autorizar | cumple | `Origin` y `Referer` son solo una defensa adicional al token CSRF (`Middleware.cs:38-59`). |
| 14.5.3 | CORS con allowlist estricta, sin `null` | cumple | No hay CORS habilitado: un preflight ajeno recibe 404 sin `Access-Control-Allow-Origin` [B-17]. |
| 14.5.4 | Headers de un proxy de confianza autenticados | cumple | `X-Forwarded-For` se acepta solo de `App:TrustedProxies` (`compose.yaml:72`) con `ForwardLimit = 1` (`API/Program.cs:179-191`); el Nginx interno lo sobrescribe (`NGX/web/default.conf:38`). IT `Account_rate_limit_cannot_be_bypassed_by_changing_forwarded_ip`; [B-18]. |

## Riesgos residuales

Son riesgos aceptados o pendientes. Se documentan para no ocultarlos (`docs/03-SEGURIDAD.md:51`).

**R1 — El bloqueo de cuenta lo puede provocar cualquiera que conozca el usuario (SEC-35).**

- **Cómo funciona:** el bloqueo de Identity es por cuenta: 10 fallos bloquean 15 min (`API/Program.cs:123-125`), y al bloquear se reinicia el contador. El límite por cuenta permite 5 intentos por minuto desde cualquier IP (`AppOptions.cs:23`).
- **El ataque:** un tercero que sabe el nombre de usuario puede mandar 10 contraseñas falsas en unos 2 minutos y repetir cada vez que vence el bloqueo. Así deja al propietario afuera unos 15 de cada 17 minutos, aunque él tenga la contraseña y el TOTP correctos. Además, mientras el atacante consume el cupo, el límite por cuenta también le puede devolver 429 al dueño.
- **Salida:** solo `recover-owner` por SSH levanta el bloqueo (`Cli.cs:185-186`).
- **Mitigación actual:** el nombre de usuario no se publica, aunque aparece en la URI `otpauth`, dentro del autenticador.
- **Pendiente:** no hay prueba automatizada del escenario. Mejoras posibles: bloqueo por cuenta e IP, demora creciente en lugar de bloqueo, o una excepción para dispositivos conocidos.

**R2 — La auditoría no es inviolable (SEC-55).**

- **Qué protege:** el rol runtime `alvarez_app` no puede modificar ni borrar `audit_events`: `REVOKE UPDATE, DELETE, TRUNCATE` (`Cli.cs:86`).
- **Quién sí puede alterarla** o borrarla sin dejar rastro:
  - el rol migrador, dueño de la tabla, que es el que purga a los 90 días (`Cli.cs:96-105`);
  - el superusuario de PostgreSQL `alvarez_admin`;
  - root o un miembro del grupo `docker` en el host, que tiene acceso directo al volumen.
- **Lo que falta:** no hay cadena de hashes ni copia remota de la auditoría. El `REVOKE` no tiene prueba automatizada.
- **Cómo leerla:** sirve para reconstruir lo que hizo la aplicación, no como prueba ante un administrador comprometido.

**R3 — Retención de logs.**

- **Contenedores:** rotan por tamaño (10 MB × 5, `compose.yaml:12-14`), no por antigüedad. Los 30 días que pide la especificación requieren configurar journald a mano (`docs/RUNBOOK.md:118`).
- **Nginx del host:** depende del logrotate del sistema.
- **Nginx interno:** guarda la IP completa y la query string (ver 7.1.2).
- **Auditoría:** sí se purga a los 90 días.

**R4 — Los limitadores viven en memoria y valen para una sola instancia.**

- **Afectados:** `AttemptLimiter` (`Security.cs:114-169`) y el limitador global (`API/Program.cs:193-204`). La clave HMAC de las cuentas se regenera en cada arranque.
- **Reinicio:** al reiniciar la API los contadores vuelven a cero. El bloqueo de Identity, en cambio, está en la base y persiste.
- **Escalado:** con más de una réplica, los límites se multiplicarían; habría que moverlos a almacenamiento compartido.
- **CPU:** cada verificación de contraseña cuesta unos 157 ms de CPU en esta máquina, unas 20 por segundo con 4 vCPU (anexo A). Un ataque distribuido desde muchas IP puede ocupar la CPU, porque no hay un tope global de logins concurrentes.

**R5 — Los conceptos y notas no se cifran a nivel de aplicación.**

- **Qué expone un volcado:** un volcado de la base o una copia del volumen dejan en claro conceptos, notas, importes, nombres de cuentas y categorías y los últimos 4 dígitos.
- **Mitigaciones:** la base no tiene puerto público, el rol runtime es mínimo y los backups van cifrados. El cifrado del volumen depende del proveedor.
- **Contexto:** la especificación lo deja como opcional para v1 (`docs/03-SEGURIDAD.md:47`). Si se implementara, cubriría notas y conceptos, no importes ni fechas.

**R6 — Un host comprometido lo compromete todo.**

Root en la VPS puede leer la memoria de la API: claves de Data Protection, secretos TOTP descifrados y sesiones. También los archivos de `SECRETS_DIR` y la base (`docs/03-SEGURIDAD.md:3`).

**R7 — Tráfico interno sin TLS.**

Los enlaces Nginx del host → `web` → `api` → `db` viajan en claro, por loopback y redes Docker del mismo host. Solo los puede leer alguien con acceso a ese host, y ese caso ya está cubierto por R6.

**R8 — El key ring usa RSA PKCS#1 v1.5.**

`ProtectKeysWithCertificate` envuelve las claves con RSA PKCS#1 v1.5 (`EncryptedXml` del framework) [B-6]. Ese descifrado solo ocurre localmente al arrancar, sin un oráculo remoto. Si se quisiera evitar, habría que reemplazar el cifrador del key ring.

## Anexo A — Hash de contraseñas: parámetros verificados y costo medido (SEC-05)

**Configuración** (`API/Program.cs:21-22,130-134`): `PasswordHasherCompatibilityMode.IdentityV3` con `IterationCount = 210_000`. Es el `PasswordHasher<AppUser>` de ASP.NET Core Identity 10.0.13, sin implementación propia.

**Verificado sobre la instancia que arma la propia API** [B-1]:

| Parámetro | Valor |
|---|---|
| Formato | Identity v3: marcador `0x01`, 61 bytes antes de Base64 |
| Función | PBKDF2 |
| PRF | **HMAC-SHA512**. Una rederivación independiente con `Rfc2898DeriveBytes.Pbkdf2(…, SHA512)` da la misma subclave. |
| Iteraciones | **210.000**: el mínimo que recomienda OWASP (Password Storage Cheat Sheet) para PBKDF2-HMAC-SHA512. ASVS 2.4.3 pide al menos 100.000. |
| Sal | **128 bits**, aleatoria y distinta en cada hash |
| Subclave | **256 bits** |
| Mezcla de parámetros | No hay: un hash v3 con HMAC-SHA256 se trata como desactualizado [B-3] |

**Rehash al cambiar la política.** `UserManager.CheckPasswordAsync` (`API/Api/AuthEndpoints.cs:143-144`) vuelve a calcular y guarda el hash cuando el verificador devuelve `SuccessRehashNeeded`. Se comprobó que el verificador de la API lo devuelve en tres casos:

- un hash v3 con 100.000 iteraciones;
- un hash v2 (PBKDF2-HMAC-SHA1);
- un hash v3 con HMAC-SHA256 y 210.000 iteraciones.

Ver [B-3]. El guardado del nuevo hash en `CheckPasswordAsync` es comportamiento del framework y no tiene prueba propia en el repo.

**Mismo costo para cuentas inexistentes.** Un usuario inexistente o bloqueado también ejecuta un PBKDF2 con los mismos parámetros, contra un hash ficticio (`AuthEndpoints.cs:129-142,439-443`). Así el tiempo de respuesta no revela si la cuenta existe.

**Costo medido** (23/10/2026):

- **Máquina:** contenedor de desarrollo, Intel Xeon @ 2,10 GHz, 4 vCPU, Ubuntu 24.04.5, .NET 10.0.13, build Release. Otras cargas de trabajo corrían en paralelo; la carga promedio era de 0,4 a 1,2.
- **Programa:** descartable, fuera del repo; llama al `IPasswordHasher<AppUser>` resuelto del contenedor de servicios de la API.
- **Procedimiento:** 3 iteraciones de calentamiento y 31 mediciones por proceso, en 4 procesos independientes. «Paralelo» son 32 verificaciones repartidas en 4 hilos.

| Proceso | `HashPassword`, mediana | `VerifyHashedPassword`, mediana | p90 de la verificación | Paralelo, 4 hilos |
|---|---|---|---|---|
| 1 | 156,1 ms | 156,5 ms | 178,8 ms | 24,3 verificaciones/s |
| 2 | 162,1 ms | 159,5 ms | 194,5 ms | 19,9 verificaciones/s |
| 3 | 157,4 ms | 159,2 ms | 192,1 ms | 20,1 verificaciones/s |
| 4 | 154,4 ms | 155,3 ms | 180,7 ms | 20,6 verificaciones/s |
| **Mediana de las medianas** | **156,8 ms** | **157,9 ms** | — | **20,4 verificaciones/s** |

El mínimo observado fue 150,6 ms y el máximo, 257,4 ms.

**Lectura de la medición:**

- Cada login correcto agrega unos 160 ms de CPU, un costo aceptable para el uso.
- Un solo núcleo verifica unas 6 contraseñas por segundo. El límite de 20/min por IP, que se aplica antes de PBKDF2, deja en ~3 s de CPU por minuto lo que puede consumir cada IP atacante (ver R4).
- Esta medición **no** es la de la VPS. Conviene repetirla allí con la misma metodología y documentar el resultado, por ejemplo con el SDK en un contenedor temporal. Si el costo bajara mucho de ~100 ms, habría que evaluar subir las iteraciones: los hashes existentes se migran solos con el rehash al ingresar.

## Anexo B — Observaciones de la verificación manual

El programa descartable (fuera del repo) armó la API real con `AlvarezFinanzas.Program.Build` usando estos parámetros:

- `--environment=Production`;
- un certificado RSA-3072 de prueba para el key ring;
- `App:PublicOrigin=https://localhost` y `App:TrustedProxies=127.0.0.1`;
- una cadena de conexión hacia un puerto cerrado: sin base de datos.

Las peticiones llevaron `X-Forwarded-Proto: https`.

- **B-1.** El hasher registrado es `PasswordHasher<AppUser>` con `IdentityV3` y 210.000 iteraciones. El hash decodificado tiene PRF HMAC-SHA512, 210.000 iteraciones, sal de 128 bits, subclave de 256 bits y 61 bytes. La rederivación independiente coincide. Dos hashes de la misma contraseña tienen sales distintas.
- **B-2.** El costo está en el anexo A.
- **B-3.** `VerifyHashedPassword` devuelve `SuccessRehashNeeded` para: v3 SHA-512 con 100.000 iteraciones, v2 (SHA-1) y v3 HMAC-SHA256 con 210.000.
- **B-4.** Resultados de los validadores de contraseña de Identity configurados por la API:

  | Contraseña | Resultado |
  |---|---|
  | 14 caracteres | rechazada (`PasswordTooShort`) |
  | 15 caracteres | aceptada |
  | 15 dígitos | aceptada |
  | 15 espacios | rechazada |
  | `"a"` + 13 espacios + `"b"` | aceptada |
  | 8 emojis (16 unidades UTF-16) | aceptada |
  | «contraseña de ñandú» | aceptada |

- **B-5.** El IL de `OtpNet.KeyGeneration.GenerateRandomKey` llama a `RandomNumberGenerator.Create` y `RandomNumberGenerator.GetBytes`.
- **B-6.** La clave del key ring generada es `AES_256_CBC` + `HMACSHA256`, con vigencia de 90 días. `masterKey` va cifrada con `EncryptedXmlDecryptor`, con los métodos `xmlenc#aes256-cbc` y `xmlenc#rsa-1_5`; no queda `masterKey` en claro.
- **B-7.** Encabezados en cada respuesta de la API (200, 400, 401, 403, 404, 429 y 500):
  - `Cache-Control: no-store` y `Pragma: no-cache`;
  - `X-Content-Type-Options: nosniff`;
  - `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'`;
  - `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY` y `Cross-Origin-Resource-Policy: same-origin`.

  No hay header `Server`. `X-Correlation-Id` está en todas, salvo en el 500.
- **B-8.** `GET /api/v1/entries` sin sesión responde `401 application/problem+json`, con `title` «Unauthorized», `traceId` y `correlationId`.
- **B-9.** `GET /api/v1/no-existe` responde `404` con «No encontrado.».
- **B-10.** Rechazos de `POST /api/v1/auth/login`, todos `403` con `"code":"csrf"`:
  - sin token: «Falta o no es válido el token de seguridad…»;
  - con `Origin: https://evil.example`: «Origen no permitido.»;
  - con `Referer` ajeno y sin `Origin`: ídem.
- **B-11.** `GET /api/v1/auth/csrf` emite `Set-Cookie: __Host-alvarez-csrf=…; path=/; secure; samesite=strict; httponly`.
- **B-12.** Con un token válido, un login con una propiedad desconocida (`ownerId`) responde `400`, «Bad Request», sin detalle por campo. Con `username` numérico, también `400`.
- **B-13.** Un login con `Content-Type: text/plain` responde **404**, «No encontrado.»: lo atiende la ruta comodín.
- **B-14.** Un login con una contraseña de 129 caracteres responde `401`, «Usuario o contraseña incorrectos.».
- **B-15.** Con una clave JSON duplicada (`username` dos veces) el request se procesa y llega a la base, caída en esta prueba, así que responde 500. No hay rechazo explícito.
- **B-16.** Un login válido con la base caída responde `500` con «An error occurred while processing your request.», `traceId` y `correlationId`, sin stack, SQL ni cadena de conexión.
- **B-17.** `OPTIONS` (preflight desde `https://evil.example`), `TRACE` y `PUT` sobre `/api/v1/entries` responden `404`, sin `Access-Control-Allow-Origin`.
- **B-18.** 21 logins seguidos con `X-Forwarded-For: 203.0.113.5` desde el proxy de confianza dan 20 × `401` y luego `429`, con `Retry-After: 60`. El cliente siguiente, con `X-Forwarded-For: 203.0.113.6`, recibe `401`: la API usa la IP real que informa el proxy de confianza.
- **B-19.** Forma de los resultados ejecutados con los servicios de la API:

  | Origen | Respuesta |
  |---|---|
  | `Validation.Problem()` | `400` con `title` «Revisá los datos ingresados.», `errors` por campo, `traceId` y `correlationId` |
  | `Problems.VersionConflict` | `409` con `current` |
  | `Problems.Unprocessable` | `422` |
  | `Problems.TooMany` | `429` con el título en español |

## Anexo C — Cómo repetir esta evaluación

1. **Pruebas:**
   - `dotnet test --project tests/unit/AlvarezFinanzas.UnitTests`;
   - `dotnet test --project tests/integration/AlvarezFinanzas.IntegrationTests` (necesita Docker);
   - en `src/web`: `npx tsc --noEmit && npx vitest run`;
   - E2E: `./infra/e2e/run.sh`.
2. **Hash y respuestas sin base:** un proyecto de consola fuera del repo, con `ProjectReference` a `src/api/AlvarezFinanzas.Api/AlvarezFinanzas.Api.csproj`, que:
   - llama a `AlvarezFinanzas.Program.Build(args)` con los argumentos del anexo B;
   - resuelve `IPasswordHasher<AppUser>` y mide con `Stopwatch` (calentamiento + n ≥ 31);
   - arranca la aplicación con `StartAsync` en `127.0.0.1` y envía los requests del anexo B.

   No usar datos ni secretos reales.
3. **En la VPS, una vez desplegada:**
   - testssl.sh o SSL Labs contra el dominio (9.1.2, 9.1.3);
   - `nmap -Pn -p 1-65535` desde otra máquina (1.14.1);
   - verificar los encabezados con `curl -sI` (14.4.x);
   - repetir la medición del anexo A.
4. Al cambiar el código, actualizar la fila afectada y el resumen. Sin una evidencia nueva, el estado no pasa a «cumple».
