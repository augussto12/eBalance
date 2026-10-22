# Seguridad — requisitos de implementación, no garantías

Estos controles se deben implementar y probar. El prototipo NO los implementa. Usar OWASP ASVS nivel 2 como referencia de revisión aplicable, sin afirmar certificación. No existe garantía de seguridad absoluta: una VPS comprometida con acceso root puede leer la memoria de la app y sus secretos.

## Amenazas consideradas
Robo de contraseña/sesión, fuerza bruta, phishing, XSS, CSRF, IDOR/BOLA, importaciones maliciosas, exposición accidental de DB/backups, filtrado en logs, dependencias vulnerables, cambios simultáneos y pérdida de claves/datos. TOTP reduce riesgo de credenciales robadas, pero no es resistente a phishing; una passkey puede ser una mejora posterior.

## Identidad y recuperación
- Sin registro público ni credenciales predeterminadas. Crear el propietario mediante CLI interactiva de un solo uso en entorno confiable. No contraseña en argumentos de shell, logs, historial o Compose.
- ASP.NET Core Identity para verificación/hash/lockout; no hash SHA simple ni cifrado reversible de contraseñas. Si se utiliza su PBKDF2, configurar y documentar la variante PRF, iteraciones y coste medido según recomendación OWASP vigente para esa variante; no mezclar parámetros SHA256/SHA512. Rehash al login cuando cambie política. Alternativa Argon2id solo con biblioteca mantenida y migración documentada.
- Contraseñas largas: mínimo 15, máximo al menos 64 (propuesta 128); permitir pegar y gestores; sin reglas arbitrarias de símbolos ni recortes silenciosos. Mensaje de login genérico.
- TOTP obligatorio para habilitar acceso financiero en producción. Sesión de preautenticación/enrollment separada, restringida, caduca en 5 minutos. Contraseña correcta sin MFA no permite GET /entries ni exportación.
- Confirmar TOTP antes de activar; evitar reutilización del mismo timestep por usuario de forma atómica, tolerancia temporal acotada y reloj del servidor sincronizado. Limitar intentos también durante enrollment.
- Secretos TOTP cifrados con mecanismo del framework y clave protegida separada de la DB; no asumir que Identity los cifra por defecto. Si se protege el key ring, no guardar contraseña de certificado junto al certificado sin permisos adecuados.
- Códigos de recuperación de alta entropía, mostrados una vez y guardados como hash; consumo atómico de un solo uso. Reemisión invalida los anteriores. No loguearlos.
- Cambiar contraseña/desactivar MFA/regenerar recuperación/exportar todo exige reautenticación reciente y MFA. Cambiar contraseña o recuperar cuenta revoca todas las sesiones y preauth.
- Sin servicio email en v1: recuperación por códigos o CLI operativa en la VPS con acceso SSH autorizado; nunca endpoint público de recuperación sin verificación. Documentar y auditar este procedimiento, no backdoor ni contraseña universal.

## Sesiones
- Cookie `__Host-alvarez-session`: HttpOnly, Secure, Path=/, sin Domain, SameSite=Lax (o Strict si UX verificada). Prefijo __Host requiere HTTPS y esas propiedades. Nada de tokens en localStorage/URL.
- Ticket de sesión en servidor: identificador criptográficamente aleatorio >=256 bits, no usar el fallback Math.random del prototipo. Persistir ticket protegido y metadatos de expiración/revocación. Rotar al completar login/MFA y elevación de autenticación.
- Política inicial: 30 minutos de inactividad y máximo absoluto 12 horas, sin remember-me. Renovación deslizante nunca extiende el máximo absoluto. Validación de revocación/usuario activo en cada request privado.
- Logout revoca ticket en DB y limpia cookie; logout-all revoca todos. Revocar una sesión surte efecto en siguiente petición. No confiar únicamente en borrar cookie.
- Respuestas auth y API financiera `Cache-Control: no-store`; limpiar caché TanStack Query al logout/401/cambio de usuario. Sin persistencia de query cache; sin service worker que cachee datos financieros.
- Nunca registrar Cookie, Set-Cookie, Authorization ni tokens CSRF.

## CSRF, origen y autorización
- Cookie auth requiere antiforgery del framework. Obtener token por endpoint controlado; guardar request token solo en memoria, enviarlo en header para POST/PUT/PATCH/DELETE; validar también login, MFA, logout, import/export y cambios de seguridad.
- Renovar token tras cambio de identidad. SameSite es defensa adicional, no reemplaza la validación. Verificar Origin/Referer esperado como defensa extra con fallback documentado, no confiar en Host arbitrario.
- Ningún GET modifica estado. CORS cerrado por defecto; no AllowAnyOrigin con credentials. Vite proxy local evita abrir CORS productivo.
- Default authorization policy autenticada; AllowAnonymous solo lista explícita de auth/health mínimo. Política adicional MFA-complete sobre todo acceso financiero.
- Cada consulta, agregación, exportación, búsqueda, edición y relación verifica owner_id derivado de sesión. Probar con dos usuarios aunque no exista registro público. UUID no sustituye permisos.
- DTO allowlist. Roles/owner_id/security stamp no pueden venir de campos del cliente.

## Aplicación y perímetro
- Límites por IP y cuenta normalizada (sin exponer el identificador): propuesta login 5 intentos/min por cuenta y 20/min por IP, lockout temporal tras 10 fallos; MFA 5/min; imports/exports 5/h por usuario. Ajustar con pruebas y limitar IP antes de PBKDF2 para controlar CPU. No bloqueo permanente fácil de abusar. Rate limit cuenta también aplica si no existe, respuestas genéricas. 429 y Retry-After.
- Confiar ForwardedHeaders solo del proxy conocido. Nunca habilitar confianza para cualquier red. Rate limiting usa IP real solo después de validar el proxy. Registrar configuración real de KnownProxies/KnownNetworks.
- CSP sin scripts inline, unsafe-inline para scripts ni unsafe-eval. Propuesta: default-src 'none'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; object-src 'none'. Refactorizar estilos dinámicos del gráfico a SVG/clases compatibles o nonce justificado; no relajar CSP globalmente. QR TOTP generado localmente.
- X-Content-Type-Options nosniff, Referrer-Policy no-referrer, Permissions-Policy que deshabilite cámara/micrófono/geolocalización, HSTS después de HTTPS validado. No includeSubDomains/preload sin evaluar todos los subdominios del dominio.
- React escapa texto. No insertar conceptos como HTML ni generar URLs ejecutables. EF parametrizado; cualquier SQL explícito debe ser parametrizado. Validación de fechas y rangos en backend.
- Límites de request en Nginx y API, tiempo de ejecución, paginación y cancelación de consultas. Endpoints de diagnóstico y Swagger sin exposición pública productiva.
- Errores sin stacks, SQL o secretos; correlation id para diagnóstico. Sin request/response body logging financiero.

## Datos, secretos y trazabilidad
- No almacenar CVV, PAN completo, claves bancarias ni tokens de bancos. Cuenta con nombre y, si hace falta, últimos cuatro dígitos no obligatorios.
- PostgreSQL sin puerto público, rol de runtime mínimo y rol separado de migraciones. Cifrado en reposo de volumen si la infraestructura lo permite, siempre backups cifrados. No afirmar que Docker volume está cifrado por defecto.
- Cifrar notas/conceptos sensibles a nivel de aplicación si se implementa protección ante fuga de dump, usando primitivas mantenidas y claves separadas. Definir expresamente cobertura: importes/fechas necesarios para consultas no quedan protegidos por cifrar solo notas. No venderlo como cifrado de toda la DB o end-to-end. Para v1, mínimo cifrado MFA/key ring y backups; documentar riesgo residual del dump de datos monetarios.
- Persistir key ring de Data Protection en volumen privado con permisos mínimos y cifrado explícito con certificado o proveedor de claves. Persistir en filesystem no implica cifrado automático. ApplicationName estable y diferente entre entornos; retener claves antiguas necesarias. Probar restore de datos + claves.
- Secretos fuera de Git/imágenes/build web, montar archivos de secretos read-only con permisos restrictivos. Compose secrets locales son archivos montados: no equivalen a una bóveda cifrada. Variables VITE_* son públicas.
- Auditoría de login/MFA/recuperación/logout/export/import/CRUD/revocación con actor/id/resultado/hora. No volcar registros completos. Rol runtime sin UPDATE/DELETE sobre auditoría cuando sea posible. Aclarar que admin DB/root puede alterarla; no llamarla inviolable.
- Logs con rotación y retención inicial 30 días; retener audit operativo 90 días configurable; IP minimizada. Sin analytics de terceros. Escaneo de secretos y dependencias en CI; corregir hallazgos explotables o documentar riesgo sin ocultarlo.

## Pruebas negativas obligatorias
Sin sesión → 401; preauth sin MFA → no datos; usuario B no lee/modifica/exporta/importa sobre A; CSRF faltante/ajeno → rechazo; origen ajeno rechazado; logout/revocación expira sesión; TOTP/código de recuperación no reutilizable; lockout/429 no omitibles cambiando IP solamente; XSS en concepto se renderiza como texto; import inválido no deja escritura parcial; no secretos en bundle/logs/Git; no acceso externo a DB. Registrar resultados, no solamente marcar casilleros.
