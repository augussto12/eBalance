# Álvarez Finanzas

App personal de finanzas (un solo propietario) para registrar ingresos, gastos, compras en cuotas y préstamos, entender el mes y proyectar compromisos. Reproduce el diseño aprobado de `reference/` con React y guarda todo en PostgreSQL detrás de una API ASP.NET Core con login, segundo factor TOTP y sesiones revocables.

- **Estado real, pruebas y pendientes:** [docs/IMPLEMENTATION-STATUS.md](docs/IMPLEMENTATION-STATUS.md)
- **Despliegue en la VPS, backups, restauración e incidentes:** [docs/RUNBOOK.md](docs/RUNBOOK.md)
- **Versiones fijadas y cómo actualizarlas:** [docs/VERSIONES.md](docs/VERSIONES.md)
- **Especificación original:** `docs/01`–`06`, `AGENTS.md`, `docs/00-PAQUETE-*`
- **Comparación visual con el prototipo:** `docs/visual/` (capturas con datos ficticios)

## Estructura

```
src/api/      API ASP.NET Core 10 (Domain / Application / Infrastructure / Api) + migraciones EF Core
src/web/      React 19 + TypeScript + Vite (CSS original portado sin cambios en styles/reference.css)
tests/unit/   reglas de dinero, calendario e importación (xUnit)
tests/integration/  API completa contra PostgreSQL 18 real y efímero (Testcontainers)
tests/e2e/    Playwright contra el stack Docker completo con TLS
infra/        Dockerfiles, Nginx, scripts de operación, systemd, entorno E2E
compose.yaml  producción (detrás del Nginx del host) · compose.dev.yaml desarrollo
```

## Desarrollo local

Requisitos: .NET SDK 10.0.1xx o superior, Node 24 LTS (o ≥ 22.12), Docker.

```bash
docker compose -p alvarez-finanzas-dev -f compose.dev.yaml up -d   # PostgreSQL local (127.0.0.1:5432, contraseña de desarrollo no secreta)
cd src/api/AlvarezFinanzas.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run -- migrate
printf 'una contraseña de desarrollo larga\n' | ASPNETCORE_ENVIRONMENT=Development dotnet run -- bootstrap-owner --username dev --display-name "Dev Local"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 dotnet run
# en otra terminal
cd src/web && npm ci && npm run dev               # https://localhost:5173 (certificado local autofirmado)
```

En desarrollo, el botón «Cargar ejemplo» (pie de página) agrega datos ficticios solo si `App:EnableDemoData=true` (ya activo en `appsettings.Development.json`). En producción ese endpoint no existe.

## Pruebas

```bash
dotnet test --project tests/unit/AlvarezFinanzas.UnitTests
dotnet test --project tests/integration/AlvarezFinanzas.IntegrationTests   # requiere Docker
cd src/web && npm test && npm run build
./infra/e2e/run.sh                                  # stack completo + Playwright (ver infra/e2e/README.md)
```

## Producción (resumen)

Ver [docs/RUNBOOK.md](docs/RUNBOOK.md). En corto: `cp .env.example .env` → `sudo ./infra/scripts/bootstrap-secrets.sh` → certificado TLS → `sudo ./infra/scripts/install-nginx-site.sh finanzas.tu-dominio` → `./infra/scripts/deploy.sh --first` → `./infra/scripts/bootstrap-owner.sh` → ingresar y configurar el autenticador → `./infra/scripts/backup.sh` y `./infra/scripts/restore-test.sh` **antes** de cargar datos reales.
