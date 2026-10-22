# VPS, despliegue y recuperación

## Entorno objetivo
VPS Linux propia, Docker Compose, Nginx ya posiblemente ocupado por otros proyectos. Inspeccionar configuración existente antes de cambiarla; no reemplazar nginx.conf global ni abrir puertos que afecten servicios ajenos. Dominio `finanzas.TU-DOMINIO` configurable. No contiene credenciales, IP ni dominio definitivo.

Ruta pública TLS 443 → Nginx → web estática y /api. Si Nginx del host ya existe, API publicada SOLO en 127.0.0.1:puerto elegido y DB sin ports. Si Nginx está en Compose, API/DB sin puertos host, red interna DB/API y proxy/API según necesidad. Nunca exponer API en 0.0.0.0 por comodidad. No confundir Docker networking con firewall: comprobar desde afuera los puertos efectivos.

## Archivos que debe producir el agente
- Dockerfile backend multi-stage; frontend build con lockfile y assets autoalojados; Compose dev y producción separados.
- `infra/nginx/finanzas.conf` con placeholders, HTTPS, headers, request size, /api y fallback SPA que NO capture errores API. Cache largo solo assets versionados; index sin cache persistente, API no-store.
- `.env.example` solo configuración pública/paths y placeholders; `.gitignore` para secrets, backups, db, .env, logs, archivos importados.
- Scripts ejecutables e idempotentes: preflight, bootstrap-owner (stdin oculto), migrate, deploy, backup, restore-test, restore, rotate-secrets. README con comandos reales de la implementación.
- No inventar un script que aún no existe como si estuviera listo: probar cada comando documentado.

## Configuración y secretos
Config no secreta: dominio/origen, ambiente Production, zona horaria, límites, retención. Secretos mediante archivos montados: credencial DB runtime, credencial migrador separada, certificado de Data Protection y su secreto, clave/referencia del backup. El lector `_FILE` debe implementarse expresamente; ASP.NET no convierte automáticamente cualquier variable terminada en _FILE en contenido de archivo.

Permisos mínimos en directorios de secretos y key ring; procesos no-root, sin privileged y sin docker.sock. Read-only root filesystem cuando sea compatible, tmpfs para temporales, cap_drop y no-new-privileges donde aplique. Volúmenes persistentes de DB y claves. No aplicar chmod 777 para resolver errores. Fijar imágenes a versiones/digests y plan de parches.

SSH por claves, acceso administrativo restringido; firewall permite solo lo requerido. HTTPS con renovación automática y monitorización de vencimiento. No incluir cambios de firewall automatizados sin comprobar acceso SSH para no bloquear al propietario.

## Flujo de deploy
1. Build y pruebas en entorno de desarrollo/CI sin secretos productivos. Contenedores escaneados, tags inmutables por commit.
2. Preflight: configuración obligatoria, permisos, espacio libre, TLS, conectividad interna, secretos presentes, MFA requerido, registro cerrado. Fallar cerrado si faltan claves; no generar silenciosamente claves nuevas al perder volumen.
3. Backup consistente antes de migración. Migraciones ejecutadas una sola vez por job con rol de migrador; app con rol runtime sin DDL. No Database.EnsureCreated ni drop/recreate en producción.
4. Levantar release, healthchecks y readiness internos; público expone únicamente estado mínimo. Comprobar HTTPS, sesión, /api, assets, cabeceras y ausencia de datos demo.
5. Mantener release anterior para rollback. Migraciones expand/contract; si no son compatibles, documentar rollback mediante restore con pérdida potencial desde backup y ventana de mantenimiento. Nunca restaurar automáticamente sin confirmación operacional.
6. Bootstrap propietario por consola confiable y completar MFA antes de cargar datos reales.

## Backup y restore (parte de la entrega, no opcional)
Objetivo inicial propuesto RPO <=24 h, RTO <=4 h sujeto a prueba en esta VPS. Backup diario consistente PostgreSQL, cifrado antes de salir del host; copiar fuera de la VPS a destino privado elegido por propietario. Retención propuesta 7 diarios + 4 semanales + 3 mensuales; controlar coste/espacio. Usar herramienta mantenida (p.ej. restic) y documentar versión/configuración, no inventar cifrado de shell.

Incluir datos, esquema/migraciones, configuración no secreta necesaria y material de recuperación de claves en respaldo cifrado separado o estrategia equivalente. Guardar clave de descifrado/certificado y recovery fuera del servidor en gestor seguro. No alojar única clave junto al único backup; no perder claves antiguas durante rotación. No publicar backups como assets web.

Restore-test en base aislada y red privada: restaurar dump, claves y app; comprobar integridad, conteos, sumas por moneda, apertura de datos protegidos y acceso con credenciales de prueba; invalidar sesiones restauradas antes de cualquier reapertura productiva. Test mensual y antes del primer uso real, registrar fecha/duración/resultado sin datos sensibles. Operación real exige selección explícita de destino y confirmación, no borrar producción por defecto.

Alertas locales o destino configurado explícitamente para backup fallido, disco lleno, expiración TLS y servicio caído; jamás incluir contenido financiero en mensajes. Entregar procedimiento de incidente: restringir acceso, preservar logs mínimos, revocar sesiones, rotar secretos, determinar exposición, recuperar desde copia conocida y revisar causa.

## Importación desde el sitio previo
Exportar JSON allí y subir manualmente en la nueva app autenticada; confirmar preview. No solicitar cookies ni credenciales de ChatGPT. El respaldo JSON original está en texto claro: avisar en UI, descarga bajo control del propietario y eliminar copias temporales cuando termine. Backup operativo cifrado y exportación de usuario son mecanismos distintos.
