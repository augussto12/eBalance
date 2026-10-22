# Plan de implementación y aceptación

## Fases
1. Leer referencia; inventariar repo y stack disponible; registrar decisiones/versions. Scaffolding reproducible y modelo de dominio exacto.
2. Portar UI a React sin alterar diseño; conectar inicialmente fixtures SOLO desarrollo. Mostrar comparación visual desktop/mobile. No declarar datos persistidos antes de backend.
3. Auth, MFA, sesiones, ownership y esquema/migraciones. Rechazo por defecto de endpoints privados; tests de aislamiento.
4. CRUD completo, cuotas, pagos/transferencias sin duplicación, dashboard y proyección con cálculo servidor. Edición y concurrencia, eliminación reversible, estados de error.
5. Migrador JSON legacy con preview y deduplicación; export/import nuevo versionado. Pruebas de integridad y límites.
6. Endurecimiento, imágenes/Compose/Nginx, backup/restauración, documentación y verificación final. Entregar estado real, no deploy remoto sin autorización específica.

## Casos de aceptación monetarios (datos ficticios)
- Ingreso ARS 2.100.000; gastos 86.500, 32.000, 12.500; compra 480.000/6; préstamo total 720.000/6, mismo mes: ingresos 2.100.000, gastos+cuotas 331.000, compromiso 200.000 (incluido), balance 1.769.000. Mes siguiente sin otros registros: gasto 200.000; desde mes séptimo 0. Seis cuotas exactas por plan.
- 10.000 centavos / 3 = 3334,3333,3333, suma 10.000. 1 centavo / 3: decidir rechazar planes con cuotas cero en v1 (cantidad > total_minor), mostrar error antes de guardar.
- Inicio 31/01/2028 → 29/02/2028 → 31/03/2028. Verificar cambio diciembre/enero y zona horaria sin correr fechas.
- Pagar 80.000 de tarjeta reduce caja y pendiente, no agrega gasto presupuestario al 331.000. Repetir request con misma idempotency key no duplica.
- Transferir ARS 10.000 entre cuentas propias no altera ingresos/gastos; rollback si falla una pata. No permitir cuenta ajena ni transferencia entre monedas.
- Agregar USD 100 no cambia KPIs ARS. Al filtrar USD muestra solo USD. No sumar totales de ambas monedas.
- No transformar cuotas viejas en pagadas al importar. Pagos parciales y adelantados no se pierden por cambiar mes.
- Dos ediciones concurrentes: una guarda y la otra recibe conflicto, no sobreescritura silenciosa.

## Matriz técnica
| Área | Verificación mínima |
|---|---|
| UI | Capturas locales con fixtures a 1440×900 y 390×844, comparación con reference, sin overflow global ni controles inaccesibles |
| Formularios | Alta/edición/borrado/restauración real, errores de servidor visibles, no doble submit |
| Persistencia | Reload y otro navegador con misma cuenta recuperan datos desde PostgreSQL |
| Aislamiento | Dos usuarios de test; IDs directos, listas, agregaciones, relaciones e import/export protegidos |
| Auth | Contraseña, MFA, enrollment restringido, recuperación, revocación y expiraciones |
| CSRF/XSS | Requests sin token rechazados, payload HTML mostrado como texto, CSP sin violaciones necesarias |
| Imports | Archivo inválido/límite/conflicto/reintento, sin escritura parcial ni duplicados |
| Backups | Restauración real en entorno aislado; datos+claves verificadas y sesiones revocadas |
| Deploy | Compose válido, build reproducible, health, headers TLS y ausencia de puertos DB públicos |
| Secretos | Escaneo Git/build/logs; certificados, claves y datos reales excluidos |

Tests backend unitarios de dinero/calendario y tests de integración contra PostgreSQL real efímero (no EF InMemory para constraints/transacciones). E2E Playwright sobre app local y usuarios ficticios para flujos críticos. Dependencias: auditoría NuGet y npm/pnpm con versiones fijadas. No prometer cobertura total ni auditoría independiente; reportar qué se ejecutó y lo que resta.

## Definición de terminado
La app funciona sin Sites/ChatGPT, reproduce el diseño, tiene persistencia real, login/MFA propios y todos los flujos descritos. El repo trae comandos probados para arrancar/desplegar/restaurar. Las diferencias respecto al prototipo están documentadas. No se llama producción-ready si quedan controles críticos sin implementar o restauración sin probar. Si el entorno no permite una prueba, marcarla como pendiente y explicar cómo ejecutarla, no marcar verde.
