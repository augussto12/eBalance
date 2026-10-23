# API REST `/api/v1`

Contrato de la API que usa el frontend (`src/web`). Está derivado del código, no de la especificación (primera versión: commit `6957687`, rama `implementacion-v1`; revisado después contra el código de la paginación e historial de pagos, la concurrencia entre pagos y planes, la restauración con versión y los códigos de estado unificados): si cambia un endpoint, este documento se actualiza en el mismo cambio. No hay OpenAPI ni Swagger a propósito (`src/api/AlvarezFinanzas.Api/AlvarezFinanzas.Api.csproj:6`), así que este archivo es el esquema documentado de los formularios y de las respuestas.

Rutas de código citadas: `API/` = `src/api/AlvarezFinanzas.Api/`, `WEB/` = `src/web/src/`.

Las formas de error y los encabezados de las secciones 4 y 5 se observaron ejecutando la API real (entorno `Production`) con un programa descartable, fuera del repo. El detalle está en `docs/ASVS-L2.md`, anexo B.

## 1. Convenciones

### Transporte y formato

- **Mismo origen y solo HTTPS.** Las cookies usan el prefijo `__Host-`, que el navegador solo acepta por HTTPS. No hay CORS (`API/Program.cs` no registra `AddCors`).
- **JSON en camelCase**, UTF-8, con `Content-Type: application/json` en todo cuerpo.
- **Allowlist estricta** (`API/Program.cs:207-214`):
  - una propiedad desconocida devuelve 400 (sin asignación masiva);
  - un número enviado como string, o al revés, devuelve 400;
  - el JSON admite como máximo 16 niveles de profundidad.
- **Tamaño del cuerpo:** Kestrel acepta hasta 64 KiB (`API/Program.cs:62`) y el Nginx interno también (`infra/nginx/web/default.conf:14`). La única excepción es la vista previa de importación (ver 7.10).
- **Tiempo máximo:** 30 s por request (`API/Program.cs:205`) y 15 s por comando SQL (`API/Program.cs:76`).

### Tipos

| Tipo | Formato | Reglas (servidor) |
|---|---|---|
| `uuid` | `"0199f6b2-…"` (UUID v7) | Los IDs los genera el servidor. Un ID de otro propietario se trata como inexistente. |
| `minor` | string de dígitos en **unidades menores** (centavos): `"123456"` = 1.234,56 | Entero positivo, sin signo, sin ceros a la izquierda, sin decimales ni exponente. Hasta 18 caracteres, de 1 a `100000000000000` (1.000.000.000.000,00) (`API/Domain/Money.cs:17,31-42`). Un número JSON en vez de string da 400. En las respuestas puede valer `"0"` (por ejemplo, `paidMinor`). |
| `signedMinor` | como `minor`, con `-` opcional y `"0"` permitido | Solo para `openingBalanceMinor` (`API/Api/CatalogEndpoints.cs:223-236`). `"-0"` no es válido. |
| `date` | `"YYYY-MM-DD"` | Fecha calendario sin zona horaria, entre 1900-01-01 y 2200-12-31 (`API/Api/ApiSupport.cs:56-66`, `API/Domain/FinanceCalendar.cs:46-49`). |
| `month` | `"YYYY-MM"` | Años 1900–2200 (`API/Domain/FinanceCalendar.cs:30-39`). |
| `currency` | `"ARS"` o `"USD"` | Exacto: en mayúsculas. Nunca se suman monedas distintas. |
| `timestamp` | ISO 8601 con desplazamiento, en UTC (`"2026-10-23T03:04:46.61+00:00"`) | Solo en respuestas (`createdAt`, `deletedAt`, `expiresAt`…). |
| `version` | entero de 64 bits | Concurrencia optimista: el servidor lo incrementa en cada cambio (`API/Infrastructure/AppDbContext.cs:239-261`). |

**Textos** (`API/Api/ApiSupport.cs:24-40`):

- Se recortan los espacios de los extremos.
- Un texto obligatorio no puede quedar vacío.
- El largo máximo se cuenta en caracteres UTF-16: un emoji ocupa 2.
- Los caracteres de control se rechazan. En las notas opcionales se permite `\n`.
- El texto se guarda y se devuelve tal cual, nunca como HTML: el frontend lo muestra como texto.

### Semántica común

- **Propiedad:** el propietario sale siempre de la sesión. Un ID ajeno en la ruta da `404`, sin confirmar que exista. Una referencia ajena en el cuerpo (`accountId`, `categoryId`…) da `400` con el mensaje de «no existe» del campo (`API/Api/EntryEndpoints.cs:175-192`).
- **PATCH reemplaza** todos los campos editables. No es una fusión parcial: un campo opcional omitido queda en `null`. Todos llevan `version` en el cuerpo.
- **DELETE** es un borrado reversible (papelera). Lleva `?version=N`: si falta o no coincide, devuelve `409`.
- **Restaurar:** `POST …/{id}/restore?version=N`, sin cuerpo. `N` es la versión actual del registro eliminado: la que devuelven el `DELETE` y `GET /trash` (o `GET /accounts?includeDeleted=true` y `GET /categories?includeDeleted=true` para lo archivado). **404** si el registro no está en la papelera o es ajeno; **409** con `current` si falta la versión o no coincide.
- **Conflicto de versión:** `409` con el registro actual en `current`, para que el cliente muestre los datos vigentes.
- **Orden de validación** en los endpoints con cuerpo: primero los datos (`400`), después la existencia y la propiedad (`404`), la versión (`409`) y por último las reglas de negocio (`409`/`422`).
- **`Location`:** los `201 Created` devuelven `Location: /api/v1/<recurso>/{id}`. Tienen `GET /{id}` `entries`, `plans`, `accounts` y `categories` (también lo eliminado o archivado). Para pagos y transferencias, esa URL devuelve 404.
- **Pagos aplicados:** el `409` de una regla que protege pagos vigentes (editar el cronograma de un plan, o editar o eliminar un plan o un gasto con pagos) lleva además `"code":"payments_applied"`, para que el cliente ofrezca la lista de pagos a revertir (`GET /payments?planId=` o `?entryId=`).
- **Idempotencia:** `POST /payments` y `POST /imports/{id}/commit` exigen el header `Idempotency-Key`: de 8 a 80 caracteres `[A-Za-z0-9_-]`. El frontend usa `crypto.randomUUID()` (`WEB/api/client.ts:148-150`).

## 2. Autenticación, sesión y niveles de acceso

### Cookies (todas `HttpOnly`, `Secure`, `Path=/`, sin `Domain`)

| Cookie | Contenido | SameSite | Vida |
|---|---|---|---|
| `__Host-alvarez-preauth` | Referencia aleatoria de 256 bits a una sesión de preautenticación en la base | Lax | 5 min absolutos (`App:PreAuthMinutes`) |
| `__Host-alvarez-session` | Referencia aleatoria de 256 bits a la sesión completa. En la base se guardan solo su SHA-256 y el ticket cifrado | Lax | 30 min de inactividad y 12 h absolutas, sin «recordarme» (`App:SessionIdleMinutes`, `App:SessionAbsoluteHours`) |
| `__Host-alvarez-csrf` | Mitad cookie del token antiforgery del framework | Strict | De sesión del navegador |

Código: `API/Infrastructure/Auth/DbTicketStore.cs:18-20,83-97,183-209`; `API/Program.cs:168-177`.

En cada request privado se comprueba en la base que la sesión no esté revocada, no haya superado la inactividad ni el máximo absoluto, que el usuario siga activo y que el security stamp no haya cambiado (`DbTicketStore.cs:105-132`).

### Niveles de acceso (columna «Acceso» de las tablas)

| Nivel | Requisito | Política |
|---|---|---|
| **anónimo** | Ninguno | `AllowAnonymous` explícito |
| **preauth** | Cookie de preautenticación válida. La etapa (`mfa` o `enroll`) la fija el login | `PreAuthOnly` (`API/Program.cs:149-151`) |
| **sesión** | Sesión completa con segundo factor (`amr=mfa`) | Política por defecto y de reserva (`API/Program.cs:163-166`) y `Financial` para los datos (`API/Program.cs:145-148,249`) |
| **elevada** | Sesión, más una reautenticación con contraseña y TOTP en los últimos 5 min (`App:ElevationMinutes`) | `Elevated` (`API/Program.cs:152-162`) |

- La contraseña sola **no** da acceso a datos: con la cookie de preautenticación, cualquier endpoint de **sesión** responde `401`.
- Sin la elevación vigente, un endpoint **elevado** responde `403`.

### Secuencia de ingreso

1. `GET /auth/csrf` → `token`, ligado a la identidad anónima.
2. `POST /auth/login` → `{"state":"mfa_required"}` o `{"state":"enroll_required"}`, y la cookie de preautenticación.
3. `GET /auth/csrf` → token nuevo: la identidad cambió.
4. Según el estado:
   - `mfa_required`: `POST /auth/mfa/verify` con `code` o con `recoveryCode`.
   - `enroll_required`: `POST /auth/mfa/enroll/start`, el usuario escanea el QR, y `POST /auth/mfa/enroll/confirm`.
5. `GET /auth/csrf` → token de la sesión completa.

Para operaciones elevadas: `POST /auth/reauth`, luego `GET /auth/csrf`, y dentro de los 5 minutos la operación.

Cada paso que cambia la identidad rota la referencia de sesión (`API/Api/AuthEndpoints.cs:152-156,201-203,263-266`; `DbTicketStore.cs:216-231`).

## 3. CSRF y origen

`API/Api/Middleware.cs:43-92`. Se aplica a **todo** `POST`, `PUT`, `PATCH` y `DELETE` bajo `/api`, incluidos `login`, `logout` y las rutas inexistentes:

1. **Origen:**
   - Si hay `Origin`, tiene que ser exactamente `App:PublicOrigin`, sin distinguir mayúsculas.
   - Si no hay `Origin` pero sí `Referer`, este tiene que empezar con `App:PublicOrigin + "/"`.
   - Si no viene ninguno de los dos, decide solo el token (fallback documentado en el código).
2. **Token:** el header `X-CSRF-TOKEN` lleva el token de `GET /auth/csrf`, junto con la cookie `__Host-alvarez-csrf`. El token queda ligado a la identidad actual, así que hay que pedirlo de nuevo después de: login, verificación MFA, confirmación del alta, reautenticación, logout, cambio de contraseña y reinicio de MFA.
3. **Rechazo:** `403` con `"code":"csrf"`. El título es «Origen no permitido.» o «Falta o no es válido el token de seguridad. Recargá la página.». Ante ese 403, el frontend pide un token nuevo y reintenta una vez (`WEB/api/client.ts:104-107`).

El frontend guarda el token solo en memoria (`WEB/api/client.ts:41-59`).

## 4. Límites

| Límite | Valor por defecto | Clave | Código |
|---|---|---|---|
| Global de la API | 300 requests/min | IP real | `API/Program.cs:193-204` (ventana fija, `Retry-After: 60`) |
| Login por IP | 20/min | IP real (se evalúa **antes** de PBKDF2) | `API/Api/AuthEndpoints.cs:115-118`; `App:LoginPerIpPerMinute` |
| Login por cuenta | 5/min | HMAC del usuario normalizado; se aplica aunque la cuenta no exista | `AuthEndpoints.cs:126-127`; `API/Infrastructure/Auth/Security.cs:146-150`; `App:LoginPerAccountPerMinute` |
| Bloqueo de cuenta | 10 fallos → 15 min | Usuario. Cuentan los fallos de contraseña, de código MFA, de alta y de reautenticación | `API/Program.cs:123-125` |
| Verificación MFA y confirmación del alta | 5/min, cupo compartido | Usuario | `AuthEndpoints.cs:166,236`; `App:MfaPerMinute` |
| Inicio de alta MFA | 5/min | `enroll:` + usuario | `AuthEndpoints.cs:218` |
| Reautenticación | 5/min | `reauth:` + usuario | `AuthEndpoints.cs:337` |
| Vista previa de importación y exportación | 5/h, cupo compartido | Usuario | `API/Api/ImportExportEndpoints.cs:40,453`; `App:ImportExportPerHour` |
| Login en el Nginx del host | 30/min, ráfaga de 10 | IP | `infra/nginx/finanzas.conf:10,55-60` |
| Paginación | `page` desde 1; `pageSize` de 1 a 100 en `entries` y `movements`, de 1 a 200 en `payments` (50 por defecto). Fuera de rango se ajusta, no da error | — | `API/Api/EntryEndpoints.cs` (`List`), `API/Api/ReportEndpoints.cs` (`Movements`), `API/Api/CashEndpoints.cs` (`ListPayments`) |
| Listas sin paginar | planes 500, transferencias 200, papelera 100 por tipo | — | ver cada endpoint |

- **Respuesta 429:** los límites propios responden `429` con `Retry-After` en segundos y el título «Demasiados intentos. Esperá un momento y volvé a probar.» (`API/Api/ApiSupport.cs:127-131`).
- **Alcance:** los limitadores viven en memoria y valen para **una sola instancia** de la API (`Security.cs:114-117`).

## 5. Formato de errores

Todos los errores son `application/problem+json` (RFC 9457) con `correlationId`, igual al header `X-Correlation-Id`, y `traceId`. Nunca incluyen stack trace, SQL ni secretos (`API/Program.cs:68-72`, `API/Api/Middleware.cs:13-18`).

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Revisá los datos ingresados.","status":400,
 "errors":{"concept":["El concepto es obligatorio."],"amountMinor":["El importe debe ser un número positivo de hasta 1.000.000.000.000,00."]},
 "traceId":"…","correlationId":"…"}
```

| Código | Cuándo | Cuerpo |
|---|---|---|
| 400 | Validación de negocio | `title`: «Revisá los datos ingresados.» y `errors`: `{campo: [mensajes]}`. Los campos son camelCase; en asignaciones de pago, `allocations[i].amountMinor`; en headers, `idempotencyKey`. |
| 400 | JSON mal formado, propiedad desconocida, tipo incorrecto, cuerpo obligatorio ausente, query o GUID de query no convertible | `title`: «Bad Request», sin detalle por campo |
| 401 | Sin sesión, sesión vencida o revocada, preauth intentando datos | `title`: «Unauthorized». Nunca HTML ni redirección. |
| 401 | Credenciales o códigos incorrectos | `title` genérico: «Usuario o contraseña incorrectos.», «El código no es válido o ya fue usado.» o «Contraseña o código incorrectos.» |
| 403 | CSRF u origen | `code`: `"csrf"` |
| 403 | Falta la elevación, o etapa de preauth equivocada | `title`: «Forbidden» o «Paso de autenticación no válido.» |
| 404 | ID inexistente o ajeno, ruta inexistente, método no mapeado, GUID inválido en la ruta, `Content-Type` no JSON en un endpoint con cuerpo JSON (ver nota) | `title`: «No encontrado.» |
| 409 | Versión distinta (`current` con el registro vigente), duplicado, regla que protege pagos aplicados (`code`: `"payments_applied"`), escritura simultánea que choca con otra (conflicto de serialización, también en el `COMMIT`), importación ya confirmada o caducada | `title` y, si aplica, `current` o `code` |
| 413 | Archivo de importación mayor que `App:ImportMaxBytes` | `title`: «El archivo supera el tamaño máximo.» |
| 415 | Vista previa de importación sin `application/json` | `title`: «El archivo debe ser JSON.» |
| 422 | Regla de negocio sobre datos bien formados | `title` (en la vista previa, también `errors`: lista de textos) |
| 429 | Límite de intentos | `title` y header `Retry-After` |
| 500 | Error inesperado | `title`: «An error occurred while processing your request.», más `correlationId`. Sin detalle. En este caso no viaja el header `X-Correlation-Id`. |
| 503 | `GET /api/health/ready` sin base de datos | Problem genérico |

Notas observadas:

- **Ruta comodín:** `app.Map("/api/{**rest}")` (`API/Program.cs:260`) atiende cualquier ruta o método que no coincida, así que un `PUT`, un `OPTIONS` o un `POST` con `Content-Type: text/plain` sobre un endpoint JSON devuelven **404**, no 405 ni 415.
- **Respuestas de Nginx:** los errores que genera Nginx antes de llegar a la API son páginas HTML propias de Nginx: 413 por exceder `client_max_body_size`, 429 de `limit_req` en el login y 502/504 si la API no responde.

Todas las respuestas de la API llevan:

- `Cache-Control: no-store` y `Pragma: no-cache`;
- `X-Content-Type-Options: nosniff`;
- `Referrer-Policy: no-referrer`;
- `X-Frame-Options: DENY`;
- `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'`;
- `Cross-Origin-Resource-Policy: same-origin`.

No envían header `Server` (`API/Api/Middleware.cs:21-36`, `API/Program.cs:61`).

## 6. Resumen de rutas

CSRF «sí» = header `X-CSRF-TOKEN` y `Origin`/`Referer` válidos (sección 3).

| Método | Ruta | Acceso | CSRF | Éxito |
|---|---|---|---|---|
| GET | `/auth/csrf` | anónimo | no | 200 |
| GET | `/auth/me` | anónimo | no | 200 |
| POST | `/auth/login` | anónimo | sí | 200 |
| POST | `/auth/mfa/verify` | preauth (`mfa`) | sí | 200 |
| POST | `/auth/mfa/enroll/start` | preauth (`enroll`) | sí | 200 |
| POST | `/auth/mfa/enroll/confirm` | preauth (`enroll`) | sí | 200 |
| POST | `/auth/logout` | anónimo | sí | 204 |
| POST | `/auth/logout-all` | sesión | sí | 204 |
| GET | `/auth/sessions` | sesión | no | 200 |
| DELETE | `/auth/sessions/{id}` | sesión | sí | 204 |
| POST | `/auth/reauth` | sesión | sí | 200 |
| POST | `/auth/password/change` | elevada | sí | 204 |
| POST | `/auth/recovery-codes/regenerate` | elevada | sí | 200 |
| POST | `/auth/mfa/reset` | elevada | sí | 204 |
| PATCH | `/auth/me` | sesión | sí | 200 |
| GET | `/accounts` | sesión | no | 200 |
| GET | `/accounts/{id}` | sesión | no | 200 |
| POST | `/accounts` | sesión | sí | 201 |
| PATCH | `/accounts/{id}` | sesión | sí | 200 |
| DELETE | `/accounts/{id}?version=` | sesión | sí | 200 |
| POST | `/accounts/{id}/restore?version=` | sesión | sí | 200 |
| GET | `/categories` | sesión | no | 200 |
| GET | `/categories/{id}` | sesión | no | 200 |
| POST | `/categories` | sesión | sí | 201 |
| PATCH | `/categories/{id}` | sesión | sí | 200 |
| DELETE | `/categories/{id}?version=` | sesión | sí | 200 |
| POST | `/categories/{id}/restore?version=` | sesión | sí | 200 |
| GET | `/entries` | sesión | no | 200 |
| GET | `/entries/{id}` | sesión | no | 200 |
| POST | `/entries` | sesión | sí | 201 |
| PATCH | `/entries/{id}` | sesión | sí | 200 |
| DELETE | `/entries/{id}?version=` | sesión | sí | 200 |
| POST | `/entries/{id}/restore?version=` | sesión | sí | 200 |
| POST | `/plans/preview` | sesión | sí | 200 |
| GET | `/plans` | sesión | no | 200 |
| GET | `/plans/{id}` | sesión | no | 200 |
| GET | `/plans/{id}/installments` | sesión | no | 200 |
| POST | `/plans` | sesión | sí | 201 |
| PATCH | `/plans/{id}` | sesión | sí | 200 |
| POST | `/plans/{id}/adjust-pending` | sesión | sí | 200 |
| DELETE | `/plans/{id}?version=` | sesión | sí | 200 |
| POST | `/plans/{id}/restore?version=` | sesión | sí | 200 |
| GET | `/obligations` | sesión | no | 200 |
| GET | `/payments` (paginado) | sesión | no | 200 |
| POST | `/payments` (+ `Idempotency-Key`) | sesión | sí | 201 (200 si se repite) |
| DELETE | `/payments/{id}?version=` | sesión | sí | 200 |
| GET | `/transfers` | sesión | no | 200 |
| POST | `/transfers` | sesión | sí | 201 |
| DELETE | `/transfers/{id}?version=` | sesión | sí | 200 |
| POST | `/transfers/{id}/restore?version=` | sesión | sí | 200 |
| GET | `/dashboard` | sesión | no | 200 |
| GET | `/projection` | sesión | no | 200 |
| GET | `/movements` | sesión | no | 200 |
| GET | `/trash` | sesión | no | 200 |
| POST | `/imports/preview` | sesión | sí | 200 |
| POST | `/imports/{id}/commit` (+ `Idempotency-Key`) | sesión | sí | 200 |
| POST | `/exports` | elevada | sí | 200 (archivo) |
| POST | `/dev/demo` | sesión, solo en Development con `App:EnableDemoData` | sí | 200 |

Fuera de `/api/v1`, ambos anónimos:

- `GET /api/health` → `200 {"status":"ok"}`;
- `GET /api/health/ready` → `200 {"status":"ok"}`, o `503` si no hay conexión a la base (`API/Program.cs:243-245`).

Los endpoints de datos se registran en `API/Program.cs:247-257`. Todos los de sesión responden `401` si falta la sesión.

## 7. Endpoints

### 7.1 Autenticación (`API/Api/AuthEndpoints.cs`)

**`GET /auth/csrf`.** Respuesta: `{"token": string}`. Emite la cookie `__Host-alvarez-csrf`. El token queda ligado a la identidad actual: anónima, preauth o sesión (`:32-38`).

**`GET /auth/me`** (`:77-110`). Responde 200 con una de estas formas:

- `{"state":"anonymous"}`
- `{"state":"mfa_required"}`
- `{"state":"enroll_required"}`
- `{"state":"authenticated","user":{"id":uuid,"username":string,"displayName":string,"initials":string,"timeZone":string,"version":int64},"recoveryCodesRemaining":int,"elevatedUntil":timestamp|null,"demoEnabled":bool}`

**`POST /auth/login`** (`:112-159`)

| Campo | Tipo | Reglas |
|---|---|---|
| `username` | string | Obligatorio. Se recortan espacios. De 1 a 64 caracteres; si no, responde 401 genérico. |
| `password` | string | Obligatorio. No se recorta. De 1 a 128 caracteres; si no, responde 401 genérico. |

- **200:** `{"state":"mfa_required"|"enroll_required"}`, con la cookie de preautenticación. Cierra la sesión o preauth anterior del navegador.
- **401:** siempre «Usuario o contraseña incorrectos.»: usuario inexistente o inactivo, contraseña incorrecta, cuenta bloqueada o largo inválido. El costo de PBKDF2 se iguala con un hash ficticio (`:129-142,439-443`).
- **429:** límite por IP o por cuenta.

**`POST /auth/mfa/verify`** (preauth, etapa `mfa`) (`:161-211`)

| Campo | Tipo | Reglas |
|---|---|---|
| `code` | string | TOTP de 6 dígitos. Ventana de ±1 paso de 30 s. No se acepta dos veces el mismo paso ni uno anterior (`API/Infrastructure/Auth/Security.cs:47-67`). |
| `recoveryCode` | string | Opcional. Si viene y no está vacío, **reemplaza** a `code`. Hasta 40 caracteres; los guiones y espacios se ignoran. |

- **200:** `{"state":"authenticated","usedRecoveryCode":bool,"recoveryCodesRemaining":int}`, con la cookie de sesión. Usar un código de recuperación revoca **todas** las demás sesiones del usuario.
- **403:** etapa equivocada.
- **401:** «El código no es válido o ya fue usado.», también con la cuenta bloqueada.
- **429.**

**`POST /auth/mfa/enroll/start`** (preauth, etapa `enroll`; sin cuerpo) (`:213-229`)

- **200:** `{"otpauthUri":string,"secret":string}` (Base32, 160 bits).
- El secreto pendiente vale 10 min. Cada llamada lo reemplaza.
- El QR se genera en el navegador, no en el servidor.
- Errores: **403** (etapa equivocada), **429**.

**`POST /auth/mfa/enroll/confirm`** (preauth, etapa `enroll`) (`:231-269`)

| Campo | Tipo | Reglas |
|---|---|---|
| `code` | string | TOTP de 6 dígitos del secreto pendiente |

- **200:** `{"state":"authenticated","recoveryCodes":[10 strings con formato XXXX-XXXX-XXXX-XXXX]}`. Los códigos se muestran **una sola vez**. Revoca las sesiones anteriores y emite la cookie de sesión.
- **422:** «La configuración caducó. Generá un código QR nuevo.»: no hay secreto pendiente, o pasaron más de 10 min.
- **401:** código incorrecto.
- **409:** no se pudo guardar.
- **403**, **429**.

**`POST /auth/logout`** (anónimo; sin cuerpo) (`:271-278`). **204** siempre. Revoca en la base la sesión y la preauth del navegador y borra las cookies.

**`POST /auth/logout-all`** (sesión; sin cuerpo) (`:280-287`). **204.** Revoca **todas** las sesiones y preauth del usuario, incluida la actual.

**`GET /auth/sessions`** (`:299-316`). **200:** lista de las sesiones completas activas, ordenadas por `lastSeenAt` descendente:

`[{"id":uuid,"createdAt":timestamp,"lastSeenAt":timestamp,"expiresAt":timestamp,"userAgent":string|null,"ipPrefix":string|null,"current":bool}]`

`userAgent` llega truncado a 160 caracteres. `ipPrefix` es la IP minimizada a /24 (IPv4) o /48 (IPv6).

**`DELETE /auth/sessions/{id}`** (`:318-330`). **204.** También puede revocar la sesión actual.

- **404:** la sesión no existe, es de otro usuario o ya estaba revocada.

**`POST /auth/reauth`** (sesión) (`:333-366`)

| Campo | Tipo | Reglas |
|---|---|---|
| `password` | string | De 1 a 128 caracteres |
| `code` | string | TOTP de 6 dígitos (consume el paso) |

- **200:** `{"elevatedUntil":timestamp}`. Rota la cookie de sesión sin extender el máximo absoluto. Después, pedir un token CSRF nuevo.
- **401:** «Contraseña o código incorrectos.», también con la cuenta bloqueada.
- **429.**

**`POST /auth/password/change`** (elevada) (`:368-393`)

| Campo | Tipo | Reglas |
|---|---|---|
| `currentPassword` | string | Obligatorio, hasta 128 caracteres |
| `newPassword` | string | De 15 a 128 caracteres, contados en unidades UTF-16. Sin reglas de composición. |

- **204:** revoca todas las sesiones, incluida la actual. Hay que volver a ingresar.
- **400:** `errors.newPassword` o `errors.currentPassword` («La contraseña actual no es correcta.»).

**`POST /auth/recovery-codes/regenerate`** (elevada; sin cuerpo) (`:395-401`). **200:** `{"recoveryCodes":[10 strings]}`. Los anteriores dejan de valer.

**`POST /auth/mfa/reset`** (elevada; sin cuerpo) (`:403-420`). **204.**

- Borra el secreto TOTP y los códigos de recuperación, y revoca todas las sesiones.
- El próximo login devuelve `enroll_required`.

**`PATCH /auth/me`** (sesión) (`:422-437`)

| Campo | Tipo | Reglas |
|---|---|---|
| `displayName` | string | Obligatorio, hasta 60 caracteres |
| `version` | int64 | Obligatorio: `user.version` de `GET /auth/me` |

- **200:** `{"displayName":string,"initials":string,"version":int64}`.
- **409:** `current` = `{displayName, version}`.

### 7.2 Cuentas (`API/Api/CatalogEndpoints.cs`)

**DTO `Account`:**

`{"id","name","kind":"cash"|"bank"|"wallet"|"credit_card","currency","last4":string|null,"openingBalanceMinor":signedMinor|null,"openingDate":date|null,"version","deletedAt":timestamp|null,"cashBalanceMinor":signedMinor|null,"cashBalanceAsOf":date|null,"pendingObligationsMinor":minor|null}` (`AccountDto`)

Los campos calculados vienen en `GET /accounts` y `GET /accounts/{id}`; en las demás respuestas son `null`. Los dos usan la misma regla «hasta hoy», con el día de hoy en la zona horaria del propietario (`API/Application/Reports.cs`, `BalanceService`):

- `cashBalanceMinor`: saldo de caja al día `cashBalanceAsOf` = apertura + flujos desde la fecha de apertura y hasta hoy. Un gasto, pago, transferencia o acreditación con fecha futura todavía no cuenta. Solo para cuentas que no son tarjeta y tienen apertura; sin apertura, `null`.
- `pendingObligationsMinor`: solo tarjetas. Cuotas pendientes de sus planes + gastos impagos cargados a ella − lo pagado **hasta hoy**: lo que asignó un pago con fecha futura sigue contando como pendiente hasta esa fecha (su importe tampoco salió todavía de la cuenta de origen), así ese dinero nunca falta de los dos lados. `GET /obligations`, en cambio, ya lo descuenta: muestra lo que falta pagar.

**`GET /accounts?includeDeleted=bool`** → `Account[]`, ordenadas por nombre y moneda.

**`GET /accounts/{id}`** → `Account` con los campos calculados, también si está archivada. **404** si no existe o es ajena.

**`POST /accounts`** (`:64-99`)

| Campo | Tipo | Reglas |
|---|---|---|
| `name` | string | Obligatorio, hasta 60 caracteres. Único entre las cuentas activas del propietario con la misma moneda; si se repite, **409**. |
| `kind` | string | Obligatorio: `cash`, `bank`, `wallet` o `credit_card` |
| `currency` | currency | Obligatorio |
| `last4` | string | Opcional: exactamente 4 dígitos ASCII. Nunca el número completo. |
| `openingBalanceMinor` | signedMinor | Opcional. Si viene este campo u `openingDate`, se exigen los dos. No se admite en `credit_card`. |
| `openingDate` | date | Ver `openingBalanceMinor` |

Respuesta: **201** `Account`.

**`PATCH /accounts/{id}`** (`:101-123`). Mismos campos más `version`, que es obligatorio.

- `kind` y `currency` **se ignoran**: no se pueden cambiar.
- `last4` y el saldo inicial se reemplazan, y si se omiten quedan en `null`.
- Respuesta: **200** `Account`.
- Errores: **404**; **409** (versión o nombre duplicado); **400** en `openingBalanceMinor` si la cuenta es una tarjeta («Las tarjetas no llevan saldo inicial…»), con la misma regla y el mismo código que al crearla.

**`DELETE /accounts/{id}?version=N`** (`:125-135`). Archiva la cuenta: los registros que la usan conservan la referencia. **200** `Account` con `deletedAt`. Errores: **404**, **409**.

**`POST /accounts/{id}/restore?version=N`** (`RestoreAccount`). **200**. Errores: **404** (la cuenta no está archivada o es ajena); **409** con `current` (falta la versión o no coincide); **409** (ya hay otra cuenta activa con ese nombre y moneda).

### 7.3 Categorías (`API/Api/CatalogEndpoints.cs:152-220`)

**DTO `Category`:** `{"id","name","kind":"expense"|"income"|"any","version","deletedAt"}`.

El servidor no exige que `kind` coincida con el tipo del movimiento que usa la categoría.

**`GET /categories?includeDeleted=bool`** → `Category[]`, por nombre.

**`GET /categories/{id}`** → `Category`, también si está archivada. **404** si no existe o es ajena.

**`POST /categories`**

| Campo | Tipo | Reglas |
|---|---|---|
| `name` | string | Obligatorio, hasta 40 caracteres. Único entre las activas del propietario; si se repite, **409**. |
| `kind` | string | Opcional: `expense`, `income` o `any` (por defecto `any`) |

Respuesta: **201**.

**`PATCH /categories/{id}`** → `name` (obligatorio), `kind` (opcional; si se omite se conserva) y `version` (obligatorio). **200**. Errores: **404**, **409**.

**`DELETE /categories/{id}?version=N`** y **`POST /categories/{id}/restore?version=N`**: igual que en cuentas (al restaurar, **409** si ya hay otra categoría activa con ese nombre).

### 7.4 Movimientos simples (`API/Api/EntryEndpoints.cs`)

**DTO `Entry`:** `{"id","kind":"income"|"expense","concept","note":string|null,"amountMinor","paidMinor","currency","date","accountId":uuid|null,"accountName":string|null,"categoryId":uuid|null,"categoryName":string|null,"version","deletedAt"}` (`:24-40`)

`paidMinor` es lo ya cancelado con pagos. Solo puede ser distinto de 0 en gastos cargados a una tarjeta.

**`GET /entries`** (`:42-75`) → `{"items":Entry[],"page":int,"pageSize":int,"total":int}`, por fecha y alta descendentes.

| Query | Tipo | Efecto |
|---|---|---|
| `month` | month | Movimientos del mes |
| `from`, `to` | date | Rango inclusivo. Se combina con `month`. |
| `currency` | currency | |
| `kind` | `income` o `expense` | |
| `accountId`, `categoryId` | uuid | |
| `q` | string, hasta 90 caracteres | Busca en concepto, categoría o cuenta, sin distinguir mayúsculas (`ILIKE`). `%` y `_` se escapan. |
| `deleted` | bool | `true` devuelve solo los eliminados |
| `page`, `pageSize` | int | 1 y 50 por defecto; `pageSize` máximo 100 |

**`GET /entries/{id}`** → `Entry`. También devuelve los eliminados, con `deletedAt`. **404** si no existe o es ajeno.

**`POST /entries`** (`:89-121`)

| Campo | Tipo | Reglas |
|---|---|---|
| `kind` | string | Obligatorio: `income` o `expense` |
| `concept` | string | Obligatorio, hasta 90 caracteres |
| `amountMinor` | minor | Obligatorio |
| `currency` | currency | Obligatorio |
| `date` | date | Obligatorio. Fecha efectiva: el mes al que se asigna. |
| `accountId` | uuid | Opcional. Cuenta propia, activa y de la **misma moneda**. Un `income` no puede ir a una `credit_card`. |
| `categoryId` | uuid | Opcional. Categoría propia y activa. |
| `note` | string | Opcional, hasta 500 caracteres. Admite `\n`. |

Respuesta: **201** `Entry`.

**`PATCH /entries/{id}`** (`:123-149`). Los mismos campos, todos obligatorios como en el alta, más `version`.

- **200** `Entry`.
- **404**; **409** de versión.
- **409** con `code: "payments_applied"` si el gasto tiene pagos aplicados y se intenta bajar el importe por debajo de lo pagado o cambiar moneda, cuenta o tipo.

**`DELETE /entries/{id}?version=N`** (`:151-161`). **200**.

- **409** con `code: "payments_applied"` si tiene pagos aplicados: primero hay que revertir los pagos.

**`POST /entries/{id}/restore?version=N`.** **200**. Errores: **404** (no está en la papelera o es ajeno); **409** con `current` (falta la versión o no coincide).

### 7.5 Cuotas y préstamos (`API/Api/PlanEndpoints.cs`)

**DTO `Installment`** (`:34-44`):

`{"id","number":int,"amountMinor","paidMinor","pendingMinor","dueDate":date,"declaredPaid":bool,"status":"pending"|"partial"|"paid"|"declared_paid"}`

**DTO `Plan`** (`:46-83`):

`{"id","kind":"card"|"loan","concept","note","totalMinor","count":int,"currency","firstDueDate","lastDueDate","accountId","accountName","categoryId","categoryName","paidBeforeCount":int,"remainingOnly":bool,"disbursementMinor":minor|null,"disbursementDate":date|null,"disbursementAccountId":uuid|null,"paidMinor","pendingMinor","currentInstallment":int,"remainingInstallments":int,"nextDue":{"number","dueDate","pendingMinor"}|null,"hasPayments":bool,"finished":bool,"version","deletedAt","installments":Installment[]|null}`

- `currentInstallment` y `remainingInstallments` se calculan con la fecha de hoy en `America/Argentina/Buenos_Aires`.
- `installments` solo viene en `GET /plans/{id}`, `POST`, `PATCH` y `adjust-pending`.

**Cuerpo de alta y edición** (`:13-15,171-204`):

| Campo | Tipo | Reglas |
|---|---|---|
| `kind` | string | Obligatorio: `card` o `loan`. No se puede cambiar al editar (**422**). |
| `concept` | string | Obligatorio, hasta 90 caracteres |
| `amountMode` | string | Obligatorio. `total`: `amountMinor` es el total de **todas** las cuotas. `installment`: `amountMinor` es la cuota, y total = cuota × `count`, sin superar el máximo. |
| `amountMinor` | minor | Obligatorio |
| `count` | int (número JSON) | Obligatorio, de 1 a 360. No puede superar el total en centavos. |
| `currency` | currency | Obligatorio |
| `firstDueDate` | date | Obligatorio. La última cuota no puede pasar de 2200-12-31. |
| `accountId` | uuid | Opcional. Cuenta propia y activa, de la misma moneda. |
| `categoryId` | uuid | Opcional |
| `note` | string | Opcional, hasta 500 caracteres |
| `paidBeforeCount` | int | Opcional (0 por defecto), de 0 a `count - 1`. Las primeras N cuotas quedan como `declared_paid`, sin pago asociado (crédito en curso). |
| `remainingOnly` | bool | Opcional. No se combina con `paidBeforeCount > 0`. |
| `disbursementMinor` | minor | Opcional, solo `loan`: el capital recibido. No es ingreso presupuestario. Si viene, se exige `disbursementDate`. |
| `disbursementDate` | date | Ver `disbursementMinor` |
| `disbursementAccountId` | uuid | Opcional. No puede ser una tarjeta; misma moneda. |
| `version` | int64 | Obligatorio al editar |

Distribución exacta: base = total DIV n, y las primeras (total MOD n) cuotas llevan base + 1. Los vencimientos se anclan al día original de la primera cuota, ajustado al último día de cada mes (`API/Domain/Money.cs:80-93`, `API/Domain/FinanceCalendar.cs:15-24`).

**`POST /plans/preview`** (`:110-127`) recibe `{amountMode, amountMinor, count, firstDueDate}`, con las mismas reglas.

- **200:** `{"totalMinor","count","baseMinor","remainderMinor","installments":[{"number","amountMinor","dueDate"}]}`.
- **400**: datos, o `errors.firstDueDate` si la última cuota queda fuera de rango (la misma regla y el mismo código que al crear o editar el plan).
- No escribe nada.
- El frontend actual no lo usa: calcula la vista previa localmente con las mismas reglas (`WEB/components/RecordForm.tsx:160`), y el servidor recalcula al guardar.

**`GET /plans`** (`:129-145`) → `Plan[]`, sin `installments`, como máximo 500, por primera cuota descendente.

| Query | Valores |
|---|---|
| `kind` | `card` o `loan` |
| `currency` | currency |
| `status` | `active` o `finished`. Cualquier otro valor devuelve todos. |
| `deleted` | bool |

**`GET /plans/{id}`** → `Plan` con cuotas, también si está eliminado. **`GET /plans/{id}/installments`** → `Installment[]`. Ambos devuelven **404** si el plan no existe o es ajeno.

**`POST /plans`** → **201** `Plan` con cuotas.

**`PATCH /plans/{id}`** (`Update`). **200** `Plan`.

- Con pagos vigentes aplicados a alguna cuota, no se puede cambiar total, cantidad, primera fecha, moneda ni `paidBeforeCount`: devuelve **409** con `code: "payments_applied"` («…Usá «Ajustar pendientes» o revertí los pagos.»). Las cuotas declaradas pagadas no cuentan como pagos.
- Sin pagos vigentes (nunca pagado, o con todos sus pagos revertidos) el cronograma se recalcula: las cuotas que siguen existiendo conservan su fila y las que sobran se eliminan. Un pago revertido conserva todas sus asignaciones; la de una cuota que deja de existir queda con `installmentId: null` y conserva `planId` e `installmentNumber` (ver 7.6).
- Sí se pueden cambiar siempre concepto, nota, cuenta, categoría y desembolso.
- Errores: **404**, **409** de versión, **422** si cambia el tipo.
- **Versión y pagos:** registrar o revertir un pago a una cuota del plan también sube la `version` del plan (el pago escribe la fila del plan en su misma transacción). Un `PATCH`, `adjust-pending` o `DELETE` con la versión de antes del pago da **409** con `current`; así un borrado o una edición que se cruza con un pago en curso nunca deja un plan eliminado con pagos vigentes.

**`POST /plans/{id}/adjust-pending`** (`:277-301`)

| Campo | Tipo | Reglas |
|---|---|---|
| `pendingTotalMinor` | minor | Obligatorio. Se reparte solo entre las cuotas **sin pagos** ni declaradas pagadas. |
| `version` | int64 | Obligatorio |

- **200** `Plan`.
- **404**; **409** de versión.
- **422** en cualquiera de estos casos: no hay cuotas abiertas, el total es menor que la cantidad de cuotas abiertas, o el nuevo total supera el máximo.

**`DELETE /plans/{id}?version=N`** (`Delete`). **200**.

- **409** con `code: "payments_applied"` si el plan tiene pagos aplicados: los pagos nunca se destruyen sin revertirlos explícitamente.

**`POST /plans/{id}/restore?version=N`.** **200**. Errores: **404** (no está en la papelera o es ajeno); **409** con `current` (falta la versión o no coincide).

### 7.6 Obligaciones y pagos (`API/Api/CashEndpoints.cs`)

**`GET /obligations?currency=&accountId=&planId=`** (`:64-73`). Devuelve lo pendiente de una tarjeta o de un plan.

- `currency` es obligatorio, y además `accountId` o `planId`; si falta, **400**.
- Con `accountId`: las cuotas de los planes cargados a esa tarjeta y sus gastos impagos.
- Con `planId`: las cuotas de ese plan.

Respuesta **200**, ordenada por vencimiento:

`[{"type":"installment"|"entry","id":uuid,"concept","dueDate","amountMinor","pendingMinor","planId":uuid|null,"number":int|null,"count":int|null}]`

**DTO `Payment`** (`PaymentDto`):

`{"id","sourceAccountId","sourceAccountName","date","currency","amountMinor","note","allocations":Allocation[],"version","createdAt":timestamp,"reversedAt":timestamp|null,"deletedAt":timestamp|null}`

**`Allocation`:** `{"installmentId":uuid|null,"entryId":uuid|null,"amountMinor","planId":uuid|null,"planKind":"card"|"loan"|null,"concept":string|null,"installmentNumber":int|null,"installmentCount":int|null,"dueDate":date|null}`

- Cada asignación dice qué canceló: una cuota (plan, número de cuota de `installmentCount`, vencimiento) o un gasto con tarjeta (`entryId`, su concepto y su fecha). Vienen ordenadas por vencimiento y número.
- `reversedAt` (igual a `deletedAt`) es la fecha de reversión. Un pago revertido conserva su fila y **todas** sus asignaciones: `amountMinor` siempre es la suma de sus asignaciones.
- Si después de revertir el pago se acortó el plan y la cuota dejó de existir, la asignación queda con `installmentId: null` y conserva `planId`, `planKind`, `concept` e `installmentNumber`; `installmentCount` y `dueDate` vienen en `null`.

**`GET /payments`** (`ListPayments`) → `{"items":Payment[],"page":int,"pageSize":int,"total":int}`, como `GET /entries`, ordenado por `date`, `createdAt` e `id` descendentes (orden estable entre páginas). `total` cuenta todos los pagos que cumplen los filtros.

| Query | Tipo | Efecto |
|---|---|---|
| `from`, `to` | date | Fecha del pago, rango inclusivo |
| `accountId` | uuid | Cuenta de origen |
| `planId` | uuid | Pagos con alguna asignación a cuotas de ese plan, incluidas las cuotas que ya no existen (pagos revertidos) |
| `installmentId` | uuid | Pagos con alguna asignación a esa cuota |
| `entryId` | uuid | Pagos con alguna asignación a ese gasto con tarjeta |
| `targetAccountId` | uuid | Tarjeta pagada: cuotas de planes cargados a ella o gastos cargados a ella |
| `kind` | `card` o `loan` | `card`: cuotas de planes de tarjeta y gastos con tarjeta; `loan`: cuotas de préstamos. Otro valor da **400** |
| `currency` | currency | |
| `includeReversed` | bool | `true` incluye los revertidos; por defecto solo los vigentes |
| `page`, `pageSize` | int | 1 y 50 por defecto; `pageSize` máximo 200. Fuera de rango se ajusta (una página después de la última viene vacía, con el `total`). Un valor que no es entero da **400** |

Un filtro con formato inválido (`from`, `to`, `currency`, `kind`) da **400** por campo.

**`POST /payments`** (`:106-228`). Header obligatorio `Idempotency-Key`. Todo el pago se aplica en una transacción `Serializable`.

| Campo | Tipo | Reglas |
|---|---|---|
| `sourceAccountId` | uuid | Obligatorio. Cuenta propia y activa, que **no** sea tarjeta, de la misma moneda. |
| `date` | date | Obligatorio |
| `currency` | currency | Obligatorio |
| `amountMinor` | minor | Obligatorio |
| `targetAccountId` | uuid | Opcional. Tiene que ser una `credit_card` propia. |
| `planId` | uuid | Opcional. Plan propio y activo. |
| `allocations` | array, hasta 500 | Opcional. Cada elemento lleva `installmentId` **o** `entryId` (exactamente uno) y `amountMinor`. Sin repetir obligaciones. La suma tiene que ser igual a `amountMinor` y ninguna asignación puede superar lo pendiente. Solo se aceptan cuotas de planes activos de la misma moneda y gastos cargados a una tarjeta. |
| `note` | string | Opcional, hasta 200 caracteres |

- Tiene que venir al menos uno de `targetAccountId`, `planId` o `allocations`.
- **Sin `allocations`**, el importe se aplica a las obligaciones más antiguas del plan (si vino `planId`) o de la tarjeta. Si sobra importe, **422**: la misma regla y el mismo código que una asignación explícita que supera lo pendiente.
- **Idempotencia:**
  - la misma clave con el mismo cuerpo devuelve **200** con el pago ya registrado, aunque después se haya revertido;
  - la misma clave con otro cuerpo devuelve **422**.

Respuestas:

- **201** `Payment`.
- **400** por campo: datos y `Idempotency-Key` (`idempotencyKey`); cuenta de origen inexistente, ajena, de otra moneda o tarjeta (`sourceAccountId`); `targetAccountId` que no es una tarjeta propia; plan inexistente o ajeno (`planId`); y en `allocations`, cuotas o gastos inexistentes, ajenos, de otra moneda o (gastos) no cargados a una tarjeta. Igual que cualquier otra referencia.
- **422**: asignaciones que no suman el importe o que superan lo pendiente (también la asignación automática), o clave de idempotencia repetida con otro cuerpo.
- **409**: «Otro cambio se guardó al mismo tiempo…»: choque con otra escritura simultánea (conflicto de serialización al leer, al guardar o en el `COMMIT`; nunca 500). No se guardó nada; reintentar con la misma clave.

El pago reduce la caja de la cuenta origen y el pendiente de las obligaciones. **No** crea un segundo gasto presupuestario. También sube la `version` de cada plan cuyas cuotas paga (ver 7.5). Un pago con fecha futura ya se descuenta en `GET /obligations` (lo que falta pagar), pero no mueve la caja ni baja `pendingObligationsMinor` hasta su fecha (ver 7.2).

**`DELETE /payments/{id}?version=N`** (`ReversePayment`). Revierte el pago: devuelve el pendiente a cada obligación, sube la `version` de los planes afectados y conserva el registro con `reversedAt` y todas sus asignaciones. **200** `Payment`. Errores: **404** (no existe, es ajeno o ya está revertido); **409** (versión distinta, o choque con otra escritura simultánea). No se puede restaurar.

### 7.7 Transferencias (`API/Api/CashEndpoints.cs:278-345`)

**DTO `Transfer`:** `{"id","fromAccountId","fromAccountName","toAccountId","toAccountName","currency","amountMinor","date","note","version","deletedAt"}`.

**`GET /transfers?from=&to=&deleted=bool`** → `Transfer[]`, como máximo 200.

**`POST /transfers`**

| Campo | Tipo | Reglas |
|---|---|---|
| `fromAccountId`, `toAccountId` | uuid | Obligatorios. Cuentas propias, activas y **distintas**, de la moneda de `currency`. Ninguna puede ser tarjeta: para eso está `POST /payments`. |
| `currency` | currency | Obligatorio. En v1 no hay transferencias entre monedas. |
| `amountMinor` | minor | Obligatorio |
| `date` | date | Obligatorio |
| `note` | string | Opcional, hasta 200 caracteres |

Respuesta: **201**. Una transferencia es una sola fila, sin efecto presupuestario.

**`DELETE /transfers/{id}?version=N`** y **`POST /transfers/{id}/restore?version=N`**: igual que en cuentas.

### 7.8 Resumen, proyección, movimientos y papelera (`API/Api/ReportEndpoints.cs`)

**DTO `MonthTotals`** (`:20-29`):

`{"month","incomeMinor","expenseMinor","installmentsMinor","installmentsPendingMinor","installmentsPaidMinor","balanceMinor":signedMinor}`

- `expenseMinor` incluye las cuotas del mes.
- `balanceMinor` = ingresos − gastos. Es un balance presupuestario, no el saldo bancario.

**DTO `Occurrence`** (`:31-49`):

`{"source":"entry"|"installment","id","planId","kind","planKind":"card"|"loan"|null,"concept","amountMinor","pendingMinor","date","planStart","categoryName","accountName","installmentNumber","installmentCount","declaredPaid","version"}`

En las cuotas, `id` es el de la cuota y `version` el del plan.

| Ruta | Query (obligatorios en **negrita**) | Respuesta |
|---|---|---|
| `GET /dashboard` | **`month`**, **`currency`** | `{"month","currency","totals":MonthTotals,"series":MonthTotals[6],"categories":[{"name","amountMinor"}]×5,"recent":Occurrence[]×5}`. `series` cubre los 5 meses anteriores y el actual. `categories` trae los 5 mayores gastos del mes por categoría, con «Sin categoría» para los que no tienen. |
| `GET /projection` | **`startMonth`**, **`currency`**, `months` (de 1 a 24; 12 por defecto) | `{"currency","months":MonthTotals[]}` |
| `GET /movements` | **`month`**, **`currency`**, `q` (hasta 90 caracteres; sin distinguir mayúsculas ni acentos, cultura es-AR), `type` (`all`, `income`, `expense`, `card` o `loan`), `page`, `pageSize` (hasta 100) | `{"items":Occurrence[],"page","pageSize","total"}` |
| `GET /trash` | — | Hasta 100 de cada tipo, por `deletedAt` descendente: `[{"type":"entry"\|"card"\|"loan"\|"transfer","id","concept","currency","amountMinor","date","deletedAt","version"}]`. `version` es la que exige `POST …/{id}/restore?version=`. Las cuentas y categorías archivadas se ven con `includeDeleted=true`. |

Un `month`, `currency`, `type` o `months` inválido devuelve **400**.

### 7.9 Exportación (`API/Api/ImportExportEndpoints.cs:449-512`)

**`POST /exports`** (elevada; sin cuerpo).

- **200** `application/json` como descarga: `Content-Disposition: attachment; filename=alvarez-finanzas-export-YYYY-MM-DD.json`, con `Cache-Control: no-store`.
- **403** sin la elevación.
- **429** comparte el cupo de 5/h con las importaciones.

No se guarda copia en el servidor, y la exportación queda auditada como `export.create`. El formato se describe en 7.11. No incluye hashes de contraseña, secretos MFA, sesiones ni claves.

### 7.10 Importación (`API/Api/ImportExportEndpoints.cs:35-209`, `API/Application/ImportFormats.cs`)

**`POST /imports/preview`.** El cuerpo es el archivo JSON crudo, no un objeto envolvente. Las validaciones, en orden:

1. El límite de 5/h; si se superó, **429**.
2. `Content-Type` tiene que empezar con `application/json`; si no, **415**.
3. El tamaño: hasta `App:ImportMaxBytes` (5 MiB); si se supera, **413**. Kestrel admite 16 MiB en esta ruta y el Nginx interno, 6 MiB.
4. El contenido: hasta 10.000 registros (`App:ImportMaxRecords`) y 8 niveles de profundidad. Cualquier error da **422** con `errors` (hasta 50 textos) y no escribe nada.

**200:**

```
{"batchId":uuid,"expiresAt":timestamp,
 "summary":{"format":"legacy-v1"|"alvarez-finanzas-export","total","new","duplicates","conflicts","byTarget":{…},"byCurrency":{…}},
 "conflicts":[{"target","sourceId","concept"}] (hasta 50),"warnings":[string],
 "accountsToCreate":[{"key":"Nombre|ARS","name","currency","usedByPlans":bool}],"categoriesToCreate":[string],
 "sample":[{"target","type","concept","amountMinor","currency","date","count","category","account"}] (hasta 20)}
```

- `accountsToCreate` y `categoriesToCreate` solo se llenan con `legacy-v1`.
- La vista previa caduca a los **30 min**.
- Se guardan solo los datos normalizados y el hash del archivo, nunca el archivo. Al confirmar o caducar, los datos normalizados se borran (`API/Api/Middleware.cs:108-110`).

**`POST /imports/{id}/commit`.** Header obligatorio `Idempotency-Key`. Todo se aplica en una transacción `Serializable`.

| Campo | Tipo | Reglas |
|---|---|---|
| `confirm` | bool | Obligatorio y `true` |
| `accountKinds` | objeto | Opcional, solo `legacy-v1`. `{"Nombre\|MONEDA": "cash"\|"bank"\|"wallet"\|"credit_card"}`, con claves de hasta 70 caracteres. Las cuentas nuevas sin tipo indicado se crean como `bank`. |

- **200:** `{"batchId","status":"committed","result":{"created","duplicates","conflicts"}}`.
- **Reintento:** la misma clave sobre un lote confirmado devuelve **200** con el mismo resultado; otra clave da **409** («Esta importación ya fue confirmada.»).
- **400:** falta `confirm`, la clave no es válida o el tipo no existe.
- **404:** el lote no existe o es ajeno.
- **409** con alguno de estos títulos:
  - «La vista previa caducó. Volvé a subir el archivo.»;
  - en el formato de exportación, cualquier registro existente con contenido distinto: se rechaza todo;
  - en el formato de exportación, un pago nuevo del archivo que apunta a un plan o gasto que ya existía y ahora está en la papelera: se rechaza todo («…Restauralo y volvé a intentar.»);
  - «Otra importación se guardó al mismo tiempo. Generá una vista previa nueva.»: choque con otra escritura simultánea al leer, al guardar o en el `COMMIT` de la transacción `Serializable` (nunca 500). No se escribió nada y la vista previa sigue vigente: se puede reintentar.
- **422:** el archivo o sus datos normalizados no cumplen las reglas (se revalidan al confirmar), por ejemplo una referencia que no está en el archivo. No se escribe nada.
- `legacy-v1` importa lo nuevo y deja visibles los duplicados y conflictos, sin sobrescribir nada.
- Esta ruta no tiene límite de frecuencia propio: depende de una vista previa, que sí lo tiene.

### 7.11 Formatos de archivo

**Respaldo del prototipo (`legacy-v1`)** (`API/Application/ImportFormats.cs:129-205`). La forma general es `{"version":1,"records":[…]}`, sin otras claves. Cada registro admite solo estas claves:

| Clave | Reglas |
|---|---|
| `id` | Obligatorio, string de hasta 100 caracteres, único en el archivo. Se guarda como `source_id`. |
| `type` | `expense` o `income` crean un movimiento simple; `card` o `loan`, un plan. |
| `name` | Obligatorio, hasta 90 caracteres. Es el concepto. |
| `amount` | Número JSON positivo con hasta 2 decimales. Más decimales dan error: nunca se trunca. Es el **total** de la compra, no la cuota. |
| `currency` | `ARS` o `USD` |
| `date` | date. En un plan es la primera cuota. |
| `category` | Opcional, hasta 40 caracteres |
| `account` | Opcional, hasta 60 caracteres. Las cuentas homónimas de distinta moneda se separan. |
| `count` | Obligatorio, entero de 1 a 360. En un plan no puede superar el total en centavos. |

Las cuotas pasadas se importan como **pendientes**.

**Exportación (`alvarez-finanzas-export`, versión 2)** (`ImportExportEndpoints.cs:464-506`, `ImportFormats.cs:207-331`). Es lo que produce `POST /exports` y lo que acepta la importación para restaurar:

```
{"format":"alvarez-finanzas-export","version":2,"exportedAt":timestamp,
 "accounts":[{"id","name","kind","currency","last4"?,"openingBalanceMinor"?,"openingDate"?,"deleted"}],
 "categories":[{"id","name","kind","deleted"}],
 "entries":[{"id","kind","concept","note"?,"amountMinor","currency","date","accountId"?,"categoryId"?,"deleted"}],
 "plans":[{"id","kind","concept","note"?,"totalMinor","count","currency","firstDueDate","accountId"?,"categoryId"?,"paidBeforeCount","remainingOnly",
           "disbursementMinor"?,"disbursementDate"?,"disbursementAccountId"?,"installments":[{"number","amountMinor","dueDate","paidMinor","declaredPaid"}],"deleted"}],
 "payments":[{"id","sourceAccountId","date","currency","amountMinor","note"?,"allocations":[{"planId"?,"installmentNumber"?,"entryId"?,"amountMinor"}]}],
 "transfers":[{"id","fromAccountId","toAccountId","currency","amountMinor","date","note"?,"deleted"}]}
```

- `?` indica que el campo se omite cuando es nulo.
- Cada lista acepta solo esas claves.
- La suma de las cuotas tiene que coincidir con `totalMinor`.
- Los IDs del archivo se usan como `source_id`: el servidor crea IDs propios y nunca acepta un `owner_id` del archivo.
- Los pagos revertidos no se exportan.

### 7.12 Datos de ejemplo (solo desarrollo)

**`POST /dev/demo`** (`API/Api/DevEndpoints.cs`) recibe `{"month":month,"currency":currency}` y responde **200** `{"created":6}`.

Solo existe con `ASPNETCORE_ENVIRONMENT=Development` y `App:EnableDemoData=true`. En producción la ruta no se registra y responde **404**.

## 8. Formularios del frontend y endpoints

| Formulario (`WEB/`) | Endpoints |
|---|---|
| Ingreso, verificación y alta del autenticador (`auth/AuthPages.tsx`) | `auth/login`, `auth/mfa/verify`, `auth/mfa/enroll/start`, `auth/mfa/enroll/confirm`, `auth/logout` |
| Reautenticación (`components/Ui.tsx:139`) | `auth/reauth` |
| Alta y edición de movimiento o plan (`components/RecordForm.tsx:196-238`) | `entries`, `plans`, `accounts` (cuenta nueva en línea), `plans/{id}/adjust-pending` |
| Registrar pago (`pages/PlansPage.tsx:164-187`) | `obligations`, `payments` |
| Configuración (`pages/SettingsPage.tsx`) | `auth/me` (PATCH), `accounts`, `categories`, `transfers`, `auth/sessions`, `auth/logout-all`, `auth/password/change`, `auth/recovery-codes/regenerate`, `auth/mfa/reset`, `exports`, `imports/preview`, `imports/{id}/commit`, `trash` |
| Resumen, Proyección y Movimientos | `dashboard`, `projection`, `movements` |

El frontend valida antes de enviar para dar respuesta inmediata (`WEB/components/RecordForm.tsx:171-193`), pero la autoridad es el servidor: un formulario que se salte esas validaciones recibe los mismos `400`, `409` o `422` descritos arriba.
