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

Los Dockerfiles usan tags exactos. Para fijar por digest, reemplazar `imagen:tag` por `imagen:tag@sha256:…` en los `ARG` de `infra/docker/*.Dockerfile` y en `compose.yaml`.

## Backend (NuGet, `Directory.Packages.props` + `packages.lock.json`)

Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.13 · Microsoft.EntityFrameworkCore.Design 10.0.13 · Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 · EFCore.NamingConventions 10.0.1 · Otp.NET 1.4.1 · xunit.v3 4.0.2 · Microsoft.AspNetCore.Mvc.Testing 10.0.13 · Testcontainers.PostgreSql 4.16.0 · Microsoft.Extensions.TimeProvider.Testing 10.11.0 · herramienta dotnet-ef 10.0.13.

## Frontend (`src/web/package.json` exacto + `package-lock.json`)

react / react-dom 19.3.1 · react-router 8.5.0 · @tanstack/react-query 5.104.5 · qrcode 1.5.4 · @fontsource/manrope 5.3.0 · @fontsource/dm-sans 5.3.0 · vite 8.3.5 · @vitejs/plugin-react 6.1.2 · @vitejs/plugin-basic-ssl 2.3.0 (solo desarrollo) · typescript 7.0.2 · vitest 5.0.5.

E2E (`tests/e2e`): @playwright/test 1.64.1 · pngjs 7.0.0 · pixelmatch 8.0.0.

## Auditoría de dependencias (22/10/2026)

- `dotnet list AlvarezFinanzas.slnx package --vulnerable --include-transitive`: sin paquetes vulnerables.
- `npm audit` (src/web y tests/e2e): 0 vulnerabilidades.
- No se ejecutó escaneo de imágenes de contenedor (Trivy/Grype). Queda pendiente: ver IMPLEMENTATION-STATUS.

## Cómo actualizar

1. Mensualmente (o ante un aviso de seguridad): revisar parches de .NET 10, PostgreSQL 18, Nginx stable y Alpine.
2. Cambiar los tags/versiones, regenerar lockfiles (`dotnet restore --force-evaluate`, `npm install --save-exact`).
3. Correr las pruebas unitarias, de integración y E2E, y el CI.
4. Desplegar con `deploy.sh` (hace backup antes de migrar). Una versión mayor de PostgreSQL requiere dump/restore planificado, no un cambio de tag.
