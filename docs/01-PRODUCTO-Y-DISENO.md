# Producto y diseño

## Objetivo
Una app personal simple para registrar ingresos, gastos, compras en cuotas y préstamos, entender el mes y proyectar compromisos. Usuario programador argentino; interfaz en español rioplatense, fecha dd/MM/yyyy, zona America/Argentina/Buenos_Aires. Fechas financieras son fechas locales sin conversión UTC; timestamps de auditoría en UTC.

## Referencia visual normativa
Copiar la apariencia de `reference/`, no aproximarla con otro dashboard. Marca temporal ÁLVAREZ FINANZAS PERSONALES y monograma á con punto verde. Variables: fondo #101112, sidebar #141617, panel #191b1d, borde #2a2d30, texto #f0f2f2, secundario #91969d, acento #d2f88b. Saldo en panel con gradiente #283124 a #1c241b. Negativos #efb0a6.

Manrope en títulos/números y DM Sans en cuerpo; autoalojar WOFF2 y licencias en producción, sin Google Fonts remoto. Mantener pesos y fallback sans-serif. Sidebar desktop 235 px, main con margen 235 y padding horizontal 42 px; breakpoint 1100: sidebar 195 y padding 23; hasta 700: navegación superior horizontal, main sin margen y padding 16. Cuatro KPIs pasan a dos columnas. Gráficos y categorías pasan a una columna. Tarjetas con bordes 15 px. Modal máximo 480 px / 94vw. Tomar todos los detalles restantes del CSS original.

Conservar encabezado «Tus finanzas, en orden.», selector mensual con anterior/siguiente, filtro ARS/USD, botones verde lima y distribución: KPIs, gráfico seis meses + categorías, últimos movimientos. Reemplazar el indicador «Guardado en este navegador» por estado real de conexión y guardado del servidor. La identidad de usuario debe ser real y editable, no hardcodeada AA cuando no corresponda.

## Pantallas
1. Login y MFA, visualmente coherentes. Sin registro público.
2. Resumen: balance presupuestario mensual = ingresos registrados del mes − gastos asignados al mes − cuotas. Mostrar que NO es saldo bancario disponible. Ingresos; gastos/cuotas; comprometido en cuotas (subconjunto, no volver a sumarlo). Gráfico de seis meses y categorías.
3. Movimientos: alta, edición, búsqueda, filtros, paginación y eliminación reversible. Buscar por nombre, categoría y cuenta. Estados loading, vacío, error/reintentar, guardando, conflicto.
4. Tarjetas y cuotas: planes, cuotas actuales y restantes, cuenta asociada, próxima fecha, pendiente y pagado. No recalcular cierres de tarjeta en v1; primera fecha de vencimiento es explícita.
5. Préstamos: capital recibido opcional como ingreso de caja separado, plan de pagos con total a devolver (intereses incluidos), cuotas y estado. No inferir tasa ni sistema de amortización.
6. Proyección: doce meses desde el seleccionado, ingresos ya registrados futuros, compromisos, balance proyectado; aclarar que no adivina ingresos futuros.
7. Configuración, en espacio secundario sin alterar layout: cuentas, categorías, contraseña, MFA, sesiones, exportación/importación.

## Formularios
Tipo, concepto, importe, moneda, fecha, categoría, cuenta/tarjeta. Para plan: total de TODAS las cuotas, cantidad, primera fecha. Permitir ingresar cuota mensual y cantidad como alternativa; mostrar el total calculado antes de confirmar. Para crédito ya en curso: fecha original + cuotas pagadas, o registrar solo cuotas restantes con etiqueta explícita; no inventar historial pagado.

Validador cliente para UX y servidor como autoridad: campos requeridos, longitudes, importes positivos, moneda permitida, fechas válidas y límites. Preview de la distribución y diferencia de redondeo. Errores asociados al campo. Modal accesible con foco atrapado, Escape, labels, navegación teclado y foco devuelto al abrir/cerrar. No alert()/confirm() como UX final: modal con acción clara, especialmente eliminación/importación.

## Límites y fases
V1: carga manual + importación del respaldo JSON existente. No PDF/CSV bancarios, integración API, emails, IA, notificaciones externas, tipos de cambio, presupuestos automáticos ni gastos recurrentes. No simulador de compra extra salvo fase posterior. No prometer PWA offline: no cachear información financiera.

La app inicia vacía. Dataset demo solo opt-in en desarrollo; nunca mezclar silenciosamente datos reales/ficticios. Mostrar monedas por separado y no convertirlas ni sumarlas.
