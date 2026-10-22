# Estado de implementación

Actualizado: 22/10/2026. Rama `implementacion-v1` del repo `augussto12/eBalance`. Todo se construyó y verificó en un contenedor cloud de desarrollo (Linux, Docker 29.9, .NET SDK 10.0.113, Node 22.22, Chromium 141.0.7390.37). **No se tocó la VPS.**

## Resumen honesto

La app funciona de punta a punta en local sobre el stack de producción: TLS → Nginx → React → API → PostgreSQL, con login, TOTP obligatorio, sesiones de servidor, CRUD, cuotas, pagos, proyección, importación/exportación, backup cifrado y restauración verificada.

**No está lista para datos reales** hasta completar en la VPS: HTTPS real, `preflight.sh` en verde, copia de backup fuera del servidor y un `restore-test.sh` exitoso ahí (gates de `AGENTS.md`). No es una auditoría de seguridad: es una implementación con pruebas, que pueden no cubrir todo.

## Fases

| Fase | Estado | Evidencia |
|---|---|---|
| 1. Inventario, versiones, dominio exacto | Hecha | `docs/VERSIONES.md`; 45 pruebas unitarias |
| 2. UI React fiel a `reference/` | Hecha | CSS original portado sin cambios; diferencia de píxeles 1,1 % (1440×900) y 1,1 % (390×844) — ver abajo |
| 3. Auth, MFA, sesiones, ownership, esquema | Hecha | 28 pruebas de integración contra PostgreSQL 18 real |
| 4. CRUD, cuotas, pagos, transferencias, resumen, proyección | Hecha | Integración + E2E |
| 5. Importación del prototipo y exportación/restauración versionada | Hecha | Integración + E2E |
| 6. Endurecimiento, imágenes, Compose, Nginx, backup/restore, docs | Hecha en local | Scripts ejecutados (ver abajo); **despliegue en la VPS pendiente** |

## Comandos realmente ejecutados y resultados

| Comando | Resultado |
|---|---|
| `dotnet test --project tests/unit/AlvarezFinanzas.UnitTests` | 45/45 OK |
| `dotnet test --project tests/integration/AlvarezFinanzas.IntegrationTests` | 28/28 OK (Testcontainers, `postgres:18.6-alpine3.24`) |
| `npx vitest run` (src/web) | 19/19 OK |
| `npm run build` / `tsc --noEmit` (src/web) | OK, sin errores de tipos |
| `./infra/e2e/run.sh` (Playwright, stack completo con TLS) | 11/11 OK |
| `./infra/scripts/preflight.sh` (stack E2E levantado) | Preflight OK (incluye TLS y `check-config`) |
| `./infra/scripts/backup.sh` | OK (restic 0.19.1, `restic check` sin errores) |
| `./infra/scripts/restore-test.sh` | resultado=OK, 15–19 s; «Coincide con producción»; TOTP descifrable 1/1 |
| `./infra/scripts/restore.sh latest` (pérdida simulada) | OK: 6/6 movimientos recuperados; tablas a nombre del migrador; sesiones revocadas |
| `./infra/scripts/restore.sh latest --with-keyring` | OK |
| `./infra/scripts/rotate-secrets.sh db-app` y `keyring-cert` | OK: la API reconecta y el certificado anterior descifra las claves viejas |
| `./infra/scripts/recover-owner.sh` (stdin) | OK: MFA reiniciado, sesiones revocadas, evento `cli.recover_owner` |
| `./infra/scripts/maintenance.sh` | OK |
| `dotnet list … package --vulnerable --include-transitive` | Sin vulnerabilidades |
| `npm audit` (web y e2e) | 0 vulnerabilidades |
| Modo desarrollo (`dotnet run` + `vite` con proxy) | Login por el proxy: OK |

### Verificaciones de perímetro (stack local)

- Puertos publicados: solo `127.0.0.1:8088` (web) y, en E2E, `127.0.0.1:8443` (edge). PostgreSQL y la API sin puertos; el 5432 está cerrado desde el host.
- La red `backend` es `internal`: la base no tiene salida a internet (verificado con `wget`).
- Rol runtime: `DELETE`/`UPDATE` sobre `audit_events` → permiso denegado; `CREATE TABLE` → permiso denegado.
- Cabeceras: CSP `default-src 'none'; script-src 'self'; style-src 'self' …` sin `unsafe-inline`/`unsafe-eval`, `nosniff`, `no-referrer`, `Permissions-Policy`, `X-Frame-Options DENY`; la API responde `Cache-Control: no-store`. El E2E no registró violaciones de CSP.
- `/api/v1/entries` sin sesión → 401 JSON; rutas `/api` inexistentes → 404 JSON (nunca el HTML del SPA); `POST /api/v1/dev/demo` en producción → no existe.
- Logs de api/web/edge después del E2E: 0 apariciones del secreto TOTP, la contraseña, la cookie de sesión, el header CSRF, `Set-Cookie` o conceptos financieros.

### Pruebas negativas obligatorias (docs/03) → dónde se prueban

| Caso | Prueba |
|---|---|
| Sin sesión → 401 | `Private_endpoints_without_session_return_401_json_not_html` |
| Preauth sin MFA → sin datos ni exportación | `Login_errors_are_generic_and_password_alone…`, E2E 1 |
| Usuario B no lee/modifica/relaciona/agrega/exporta datos de A | `User_b_cannot_read_modify_relate_or_aggregate…`, `Commit_requires_confirmation_and_owner` |
| FK compuestas impiden relaciones ajenas aun salteando la API | `Database_rejects_cross_owner_references…` |
| CSRF faltante/ajeno y origen ajeno → rechazo | `Csrf_missing_foreign_token_or_foreign_origin…` |
| Logout/revocación surten efecto en el request siguiente (incluso reusando la cookie copiada) | `Logout_and_revocation_take_effect…` |
| Inactividad 30 min y máximo absoluto 12 h | `Idle_timeout_and_absolute_maximum…` |
| TOTP y código de recuperación no reutilizables | `Totp_code_cannot_be_reused…` |
| 429 por cuenta no se evita cambiando X-Forwarded-For; lockout tras 10 fallos | `Account_rate_limit_cannot_be_bypassed…`, `Lockout_after_ten_failures…` |
| XSS en concepto → texto | `Html_in_concept…` + E2E 4 (sin `<img>` en el DOM ni diálogos) |
| Importación inválida → sin escritura parcial | `Invalid_import_writes_nothing` |
| Sin mass assignment | `Unknown_json_properties_are_rejected…` |
| Sin secretos en bundle/logs/Git | `.gitignore`, verificación de logs (arriba), job `secrets` del CI (gitleaks) **aún no ejecutado** |
| Sin acceso externo a la DB | verificado en el stack local; **falta repetirlo desde afuera en la VPS** |

### Casos de aceptación monetarios (docs/05)

Todos automatizados (`FinanceTests`, `MoneyTests`, `CalendarTests`, E2E 2/6/7): balance 1.769.000 / gastos+cuotas 331.000 / comprometido 200.000; meses siguientes 200.000 y cero desde el séptimo; 10.000 centavos ÷ 3 = 3334/3333/3333; 1 centavo ÷ 3 rechazado; 31/01/2028 → 29/02 → 31/03; cambio diciembre/enero y zona horaria; pago de 80.000 que no suma gasto e idempotente; transferencia sin efecto presupuestario y rechazo entre monedas; USD separado; cuotas importadas no marcadas como pagadas; conflicto 409 en ediciones concurrentes.

## Comparación visual con `reference/`

`docs/visual/` contiene las capturas de la referencia y de la app (mismo dataset ficticio, mismas fuentes), lado a lado y el mapa de diferencias. `comparacion.json` muestra 1,09 % de píxeles distintos a 1440×900, 1,15 % a 390×844 (viewport) y 2,6 % en la página completa móvil.

Diferencias **intencionales** respecto del prototipo:

1. «Guardado en este navegador» se reemplaza por el estado real del servidor (Conectado / Guardando… / Sin conexión).
2. La tarjeta de balance aclara «No es saldo bancario disponible».
3. Avatar con iniciales reales y editables (botón hacia Perfil).
4. La barra lateral suma Configuración y Cerrar sesión, en el mismo estilo que Exportar/Restaurar. En celular quedan ocultas, igual que en el prototipo.
5. Cada fila tiene editar (✎) además de eliminar (×). Eliminar usa un modal propio y «Deshacer» en lugar de `confirm()`.
6. Movimientos suma un filtro por tipo y paginación. Tarjetas/Préstamos suman «Registrar pago» y una tabla de planes (cuota actual y restantes, próximo vencimiento, pagado, pendiente). Proyección suma la columna «Cuotas pendientes».
7. El pie muestra «Cargar ejemplo» solo en desarrollo.
8. El formulario suma: modo total/cuota mensual, vista previa exacta de la distribución, crédito en curso, capital recibido, nueva cuenta en línea y nota.
9. El gráfico es SVG (exigencia de la CSP), con las mismas medidas.
10. Pantallas nuevas con el mismo lenguaje visual: login, verificación, alta del autenticador y Configuración.

## Decisiones tomadas (sin cambiar el alcance)

- **Sesiones:** cookie `__Host-alvarez-session` con una referencia aleatoria de 256 bits protegida por Data Protection; en la base se guarda solo su SHA-256 y el ticket cifrado. Preautenticación en una cookie y esquema separados, válida 5 min. Elevación por reautenticación (contraseña + TOTP) de 5 min con rotación de la referencia, sin extender el máximo absoluto.
- **Contraseñas:** Identity v3 PBKDF2-HMAC-SHA512 con **210.000 iteraciones** (OWASP para esa variante); rehash automático al ingresar. Longitud 15–128, sin reglas de símbolos.
- **TOTP:** Otp.NET, ventana ±1 paso, anti-reutilización con un `UPDATE` condicional atómico; secreto cifrado con Data Protection. Diez códigos de recuperación de 80 bits guardados como SHA-256.
- **Key ring:** en un volumen, cifrado con un certificado RSA-3072 (PFX + contraseña) montado de solo lectura; admite certificados anteriores para rotar. En producción, la app no arranca sin certificado.
- **Pagos:** se asignan a cuotas de planes o a gastos cargados en tarjeta. Si no se indican asignaciones, se aplican a lo más antiguo. Transacción `Serializable` con `Idempotency-Key`. Revertir un pago restituye el pendiente.
- **Saldo de cuenta:** solo si se cargó un saldo inicial; se calcula como apertura + ingresos − gastos − pagos ± transferencias + capital recibido. Las tarjetas muestran el pendiente total.
- **Planes con pagos:** no se cambia el cronograma; existe «Ajustar pendientes», que redistribuye solo las cuotas sin pagos. Un plan con pagos no se elimina sin revertirlos antes.
- **Importación:** la vista previa se guarda en la base (no el archivo) y caduca a los 30 min. El formato del prototipo (v1) importa omitiendo conflictos visibles. El formato de exportación v2 se rechaza entero si hay conflictos.

## Pendiente / no probado (no marcar en verde)

1. **Despliegue en la VPS:** nada se ejecutó allí. Falta: certificado Let's Encrypt real, `install-nginx-site.sh` sobre el Nginx existente, HSTS, `nmap` externo y medir RTO.
2. **Copia de backup fuera de la VPS** (`OFFSITE_RESTIC_REPOSITORY`): implementada, no probada. Falta elegir destino.
3. **CI** (`.github/workflows/ci.yml`): escrito, todavía no corrió en GitHub (incluye gitleaks y build de imágenes). Revisar el primer run después del push.
4. `rotate-secrets.sh restic` y `db-migrator`: no ejecutados.
5. Alertas fuera del servidor: solo hay unidades systemd que fallan; falta elegir canal (`OnFailure=`).
6. Escaneo de imágenes de contenedor (Trivy/Grype): no ejecutado.
7. La prueba de ingreso interactivo sobre un backup restaurado es manual (RUNBOOK §5).
8. Accesibilidad: hay modal accesible, labels, foco y teclado, pero no se corrió una auditoría automática (axe) ni con lector de pantalla.
9. Navegadores: E2E solo en Chromium. Safari/Firefox y celulares reales sin probar.
10. Cifrado de conceptos/notas a nivel de aplicación: **no implementado** (opcional en la spec). Riesgo residual: un volcado de la base expone conceptos e importes en claro. El cifrado del volumen depende del proveedor de la VPS. Los backups sí van cifrados.
11. Rendimiento con miles de registros y concurrencia real: no medidos más allá de las pruebas.
12. Limitadores en memoria: válidos para una sola instancia de la API (así está desplegado).
13. Retención de logs: limitada por tamaño, no por 30 días, salvo configurar journald (RUNBOOK §3).
14. Fuera de alcance por especificación: CSV/PDF bancarios, notificaciones, tipos de cambio, presupuestos automáticos, recurrentes, passkeys.

## Bugs encontrados y corregidos durante la verificación

- Los tokens CSRF se emitían con identidad anónima en endpoints `AllowAnonymous`: se autentica explícitamente con el esquema combinado.
- La reautenticación no rotaba la sesión (el handler de cookies reutiliza la clave): se emite una referencia nueva explícitamente.
- `restore.sh`: `docker compose run` consumía el stdin de la confirmación, «latest» se resolvía después del backup de seguridad y la retención `keep-daily` podía borrar la instantánea elegida. Corregido: el ID se fija antes, hay `--keep-last 10` y `BACKUP_NO_PRUNE` en el backup de seguridad.
- `compose.dev.yaml` podía reemplazar el contenedor `db` de producción si existía `.env`: ahora el servicio es `devdb` y se usa un proyecto propio.
- En celular, el texto oculto «Acciones» de las tablas ensanchaba la página 191 px.
- Al cerrar sesión, `qc.clear()` desconectaba la consulta de sesión y no volvía al login.
