# Arquitectura, datos y reglas

## Stack y organización
Frontend React + TypeScript + Vite, CSS original adaptado (Tailwind opcional, nunca a costa de fidelidad), TanStack Query para datos remotos, formularios con esquema compartido/documentado. Backend ASP.NET Core estable LTS, EF Core y Npgsql de versiones compatibles, PostgreSQL soportado. Elegir y fijar versiones tras verificar fuentes oficiales, sin paquetes preview ni tags latest.

Monorepo: src/web, src/api (capas Domain, Application, Infrastructure, Api separadas lógicamente), tests/{unit,integration,e2e}, infra/{docker,nginx,scripts}, docs. No necesidad de un proyecto por cada entidad.

Mismo origen: https://finanzas.DOMINIO → Nginx sirve React; /api → backend privado. Sin CORS en producción porque no se necesita. En desarrollo Vite proxy /api con HTTPS local. PostgreSQL interno. Autenticación ASP.NET Core Identity + cookies con tickets persistidos en servidor (ITicketStore o equivalente probado), no JWT en el navegador. La sesión privada de Sites no se exporta.

## Modelo mínimo
Todos los registros financieros tienen UUID, owner_id, created_at, updated_at, version y deleted_at cuando aplique. Ownership se obtiene de la sesión, nunca del cuerpo del request.

| Entidad | Campos/reglas principales |
|---|---|
| User | Identity, MFA, estado, security stamp, zona horaria |
| Session | referencia aleatoria, usuario, ticket protegido, expiración, revocación, última actividad |
| Account | propietario, nombre, tipo cash/bank/wallet/credit_card, moneda; sin números completos de tarjeta |
| Category | propietario, nombre, tipo, archivado |
| Entry | tipo income/expense, amount_minor bigint positivo, currency, effective_date, account_id, category_id, concepto, nota opcional, status |
| InstallmentPlan | tipo card/loan, total_minor, count, first_due_date, cuenta, categoría, concepto |
| Installment | plan, número único, amount_minor, due_date, paid_at opcional, payment_id opcional |
| Transfer | cuentas origen/destino, moneda, amount_minor, fecha; una única operación atómica |
| Payment | pago a tarjeta/cuota, cuenta origen, fecha, importe y asignaciones a obligaciones |
| ImportBatch | propietario, hash archivo, versión formato, estado, resumen, idempotency_key |
| ImportedRecord | propietario, source_type, source_id, hash normalizado; unique(owner,source,source_id) |
| AuditEvent | actor, acción, recurso/id, resultado, timestamp, correlation id; sin importes ni conceptos |

FK compuestas por owner_id+id o validación equivalente en cada relación para impedir asignar cuentas/categorías ajenas. Query filters sirven como defensa extra, no única autorización. Índices owner/date/currency y constraints únicos de cuotas/imports. API no admite propiedades arbitrarias ni mass assignment.

## Semántica monetaria obligatoria
- Backend usa unidades menores enteras (centavos) con bigint y operaciones checked; API transporta amountMinor como string decimal para evitar pérdida de precisión JS. Frontend puede usar BigInt para presentación, nunca coma flotante como autoridad.
- ARS/USD: dos decimales. Validar límite de negocio y overflow. Sin sumar monedas distintas.
- Distribución exacta: base = total DIV cantidad, resto = total MOD cantidad; primeras `resto` cuotas tienen base+1. Ejemplo 100,00 / 3 → 33,34; 33,33; 33,33. Nunca 33,33 × 3 con centavo perdido.
- Anclar fechas al día ORIGINAL de primera cuota, ajustando al último día válido del mes destino: 31/01 → 28/02 (29 si bisiesto) → 31/03. No encadenar AddMonths sobre la fecha ajustada.
- Gastos simples se asignan a su fecha. Compra financiada se asigna al mes de cada cuota, no además el total al inicio. La compra total es un compromiso, no salida de caja inmediata.
- Pago de tarjeta/cuota cancela obligación y mueve caja, pero no crea un segundo gasto presupuestario. Pago parcial deja pendiente; asignaciones no superan pendiente. Operación transaccional e idempotente.
- Transferencias entre cuentas propias: efecto neto global cero; no ingreso/gasto. V1 solo misma moneda; rechazar cruce ARS/USD hasta modelar conversión.
- Balance del mes es presupuestario. Saldo de cuenta se calcula solo con apertura + flujos de caja conciliados. No mostrar un «disponible» supuesto con datos incompletos.
- Proyección usa las obligaciones previstas por fecha, aunque se hayan pagado por adelantado; estado pendiente/pagado se presenta separado. No sumar pagos como gasto.
- Edición de plan con cuotas pagadas: no reescribir historia. Bloquear modificación de total/cantidad/fecha sobre cuotas pagadas y proponer ajuste de pendientes explícito. Concurrencia optimista: 409, no último-write-wins silencioso.
- Eliminación reversible de plan y dependencias en transacción; no destruir pagos asociados sin una política explícita de reversión. Undo o papelera con restauración y auditoría.

## API orientativa /api/v1
Auth: GET csrf; POST login; POST mfa/verify; POST logout; GET me; GET sessions; DELETE sessions/{id}; POST logout-all; POST password/change. Enrollment MFA requiere sesión restringida y reautenticación; no devuelve datos financieros hasta completar alta y confirmación.

GET/POST accounts, categories, entries, plans; GET/PATCH/DELETE por ID; POST restore. GET plans/{id}/installments. POST payments y transfers. GET dashboard?month=YYYY-MM&currency=ARS; GET projection?startMonth=...&months=12&currency=...; GET entries con filtros y paginación límite 100.

POST imports/preview → datos normalizados y conflictos; POST imports/{id}/commit con confirmación e idempotency key. POST exports genera descarga autenticada y no cacheable. No enlaces públicos a archivos. PATCH con versión/If-Match y DELETE/restore con comprobación de versión.

DTO explícitos, validación server-side, ProblemDetails sin stack trace, códigos 400/401/403/404/409/422/429 coherentes. Endpoints privados sin sesión → 401, nunca devolver HTML de login. ID ajeno → 404 sin confirmar existencia.

## Migración del prototipo
Formato {version:1,records:[{id,type,name,amount,currency,date,category,account,count}]}. Ver reference/app.js. Importar expense/income como Entry y card/loan como Plan. `amount` significa TOTAL, no cuota. UUID del prototipo puede ser string no UUID por fallback HTTP; conservarlo como source_id, crear UUID propio.

Aceptar solo JSON UTF-8, máximo inicial 5 MiB y 10.000 registros (ajustable), profundidad acotada, esquema estricto, límite de strings, fechas/currency/enums y rechazo de NaN/infinito/negativos. Parsear decimales en servidor con precisión y regla explícita; si más de dos decimales, pedir corrección en preview, no truncar silenciosamente. No aceptar owner_id del archivo. Mapear cuenta/categoría por propietario; cuentas homónimas con monedas distintas se separan. Cuotas previas al día actual NO se marcan pagadas automáticamente. Preview advierte que el JSON original no conoce saldos, pagos ni vencimientos exactos.

Idempotencia por source_id y hash: misma carga no duplica; source_id con contenido cambiado → conflicto visible, sin sobrescribir automáticamente. Todo commit en transacción. El nombre/concepto se trata como texto, nunca HTML. No subidas persistidas sin necesidad: temporales privados con caducidad y limpieza. JSON export nuevo versionado incluye planes/pagos/cuentas y permite restauración completa tras preview, distinta de la migración legacy. No exportar hashes de contraseña, secretos MFA, sesiones o claves de cifrado.
