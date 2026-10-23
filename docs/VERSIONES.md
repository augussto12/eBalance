# Versiones fijadas

Verificadas el 22/10/2026 contra los registros oficiales (NuGet, npm, MCR, Docker Hub). Ninguna es preview, RC ni `latest`.

## Plataforma

| Componente | Versión | Nota |
|---|---|---|
| .NET (runtime) | 10.0.13 | LTS (soporte hasta nov-2028) |
| .NET SDK | `global.json`: 10.0.100 + `rollForward: latestFeature` | Imagen de build: `sdk:10.0.402-alpine3.24` |
| PostgreSQL | 18.6 | `postgres:18.6-alpine3.24` |
| Nginx (contenedor web) | 1.30.6 (stable) | `nginxinc/nginx-unprivileged:1.30.6-alpine3.24` |
| Node.js (build) | 24.22.1 LTS | `node:24.22.1-alpine3.24` |
| restic | 0.19.1 | binario de `restic/restic:0.19.1` |
| certbot (solo VPS, en contenedor) | 5.8.0 | `certbot/certbot:v5.8.0` fijado por digest en `infra/scripts/certbot.sh`; ≥ 5.4 es necesario para certificados de IP con webroot |
| Nginx del host (VPS) | el de la distribución | Probado con 1.24.0 (Ubuntu 24.04): sin `http2 on;`, el instalador usa `listen 443 ssl http2` |
| Trivy (solo CI / escaneo) | 0.75.0 | `aquasec/trivy:0.75.0` fijado por digest en `infra/scripts/scan-images.sh` |
| ShellCheck (solo CI) | 0.11.0 | `koalaman/shellcheck:v0.11.0` fijado por digest en `.github/workflows/ci.yml` |
| restic rest-server (solo pruebas) | 0.14.0 | `restic/rest-server:0.14.0` fijado por digest en `infra/tests/offsite-backup.sh` (repositorio externo sustituto) |

## Imágenes y digests observados

| Imagen | Digest |
|---|---|
| postgres:18.6-alpine3.24 | sha256:898c60cb390d1085bc97bdd6a1ae968757421df394b071848b52b1c09c89d6a5 |
| nginxinc/nginx-unprivileged:1.30.6-alpine3.24 | sha256:f5116ed4606f3eb015dd4a8e28cb9b351476dc0ad4bc6e9f93b19cec130cdf03 |
| node:24.22.1-alpine3.24 | sha256:06d7adf3e0ef61a13f1d28682726366f97a15aa1888c934d3ecec86c33918b53 |
| mcr.microsoft.com/dotnet/sdk:10.0.402-alpine3.24 | sha256:496914451b05ac5a24592024362ec370c3a2d3c3fe817f864e4aa55c4c194a56 |
| restic/restic:0.19.1 | sha256:136600b6ff6843d61d355f7f71f460a166429f35de6fd11b568fece3c9a4d510 |
| mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24-extra | (no registrado: obtenerlo con `docker buildx imagetools inspect`) |
| nginx:1.30.6-alpine3.24 (solo E2E) | sha256:47928bf67eb62a1863cb08d9ecb52d3e704304ba614eaee17884204677702951 |
| certbot/certbot:v5.8.0 | sha256:f70ad0adbb7e117f0fe42a63c553f28ea451edabc0148757b6efcd9735acaa20 |
| aquasec/trivy:0.75.0 | sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa |
| koalaman/shellcheck:v0.11.0 | sha256:61862eba1fcf09a484ebcc6feea46f1782532571a34ed51fedf90dd25f925a8d |
| restic/rest-server:0.14.0 | sha256:d2aff06f47eb38637dff580c3e6bce4af98f386c396a25d32eb6727ec96214a5 |

Los Dockerfiles usan tags exactos. Para fijar por digest, reemplazar `imagen:tag` por `imagen:tag@sha256:…` en los `ARG` de `infra/docker/*.Dockerfile` y en `compose.yaml`.

## Backend (NuGet, `Directory.Packages.props` + `packages.lock.json`)

Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.13 · Microsoft.EntityFrameworkCore.Design 10.0.13 · Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 · EFCore.NamingConventions 10.0.1 · Otp.NET 1.4.1 · xunit.v3 4.0.2 · Microsoft.AspNetCore.Mvc.Testing 10.0.13 · Testcontainers.PostgreSql 4.16.0 · Microsoft.Extensions.TimeProvider.Testing 10.11.0 · herramienta dotnet-ef 10.0.13.

## Frontend (`src/web/package.json` exacto + `package-lock.json`)

react / react-dom 19.3.1 · react-router 8.5.0 · @tanstack/react-query 5.104.5 · qrcode 1.5.4 · @fontsource/manrope 5.3.0 · @fontsource/dm-sans 5.3.0 · vite 8.3.5 · @vitejs/plugin-react 6.1.2 · @vitejs/plugin-basic-ssl 2.3.0 (solo desarrollo) · typescript 7.0.2 · vitest 5.0.5.

E2E (`tests/e2e`): @playwright/test 1.64.1 · pngjs 7.0.0 · pixelmatch 8.0.0.

## Auditoría de dependencias (22/10/2026)

- `dotnet list AlvarezFinanzas.slnx package --vulnerable --include-transitive`: sin paquetes vulnerables.
- `npm audit` (src/web y tests/e2e): 0 vulnerabilidades.

## Escaneo de imágenes (Trivy)

`infra/scripts/scan-images.sh [TAG]` escanea las tres imágenes que construye el repo (api, web, backup) y falla si hay vulnerabilidades HIGH o CRITICAL **con corrección publicada** que no estén en `infra/trivy-ignore.yaml`. El CI lo corre en cada push y todos los lunes (`schedule`), porque aparecen CVE nuevas sin que cambie el código. Con `SCAN_BASE=1` también informa, sin fallar, sobre `postgres` oficial (servicio `db`), que el repo no construye. Trivy corre en un contenedor sin `docker.sock` (las imágenes se le pasan con `docker save`).

Resultado del 23/10/2026 (Trivy 0.75.0, base de datos de ese día):

- **api** (`aspnet:10.0.13-alpine3.24-extra` + dependencias NuGet) y **web** (`nginx-unprivileged:1.30.6-alpine3.24`): sin hallazgos HIGH/CRITICAL corregibles.
- **backup**: el `gosu` de la imagen de postgres (Go 1.24.6, 26 avisos, uno CRITICAL) se **quitó** de la imagen de backup, que nunca arranca el servidor PostgreSQL. Quedan 22 avisos HIGH en `restic` 0.19.1 (compilado con Go 1.26.4 y dependencias con avisos), que es la última versión publicada: están en `infra/trivy-ignore.yaml`, acotados al archivo `usr/local/bin/restic` y con vencimiento el 31/01/2027. El análisis de exposición está en el encabezado de ese archivo.
- **postgres:18.6-alpine3.24** (servicio `db`, informativo): el mismo `gosu` con 26 avisos. Lo usa el entrypoint solo para bajar a uid 70 al arrancar; se corrige cuando la imagen oficial publique una versión nueva (cambiar el tag en `compose.yaml` y en `backup.Dockerfile`).

**Cuando el escaneo falla:**

1. Si hay una versión corregida de la imagen base o del paquete: actualizar el tag/la versión (Dockerfile, `compose.yaml`, lockfile), reconstruir y volver a escanear.
2. Si no hay corrección a nuestro alcance (p. ej. un binario de terceros sin versión nueva): agregar la excepción a `infra/trivy-ignore.yaml` con `id`, `paths` (el archivo afectado, nunca toda la imagen), `statement` (por qué no aplica o por qué se acepta) y `expired_at` (como máximo 3 meses). Al vencer, el CI vuelve a fallar y hay que revisarla.
3. Nunca usar `--exit-code 0`, subir el umbral de severidad ni quitar `--ignore-unfixed` para que el CI pase.

**Actualizar Trivy:** elegir una versión publicada hace al menos dos semanas, obtener su digest (`docker buildx imagetools inspect aquasec/trivy:X.Y.Z`), cambiar `TRIVY_IMAGE` en `infra/scripts/scan-images.sh` y correr el script localmente antes de subirlo. Lo mismo para ShellCheck en `.github/workflows/ci.yml`.

## Cómo actualizar

1. Mensualmente (o ante un aviso de seguridad o un escaneo fallido): revisar parches de .NET 10, PostgreSQL 18, Nginx stable, Alpine, restic y certbot (`CERTBOT_IMAGE` en `infra/scripts/certbot.sh`, con su digest).
2. Cambiar los tags/versiones, regenerar lockfiles (`dotnet restore --force-evaluate`, `npm install --save-exact`).
3. Correr las pruebas unitarias, de integración y E2E, `infra/scripts/scan-images.sh` y el CI.
4. Desplegar con `deploy.sh` (hace backup antes de migrar). Una versión mayor de PostgreSQL requiere dump/restore planificado, no un cambio de tag.
