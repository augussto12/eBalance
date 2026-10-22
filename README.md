# Álvarez Finanzas — paquete de implementación para VS Code

Preparado el 20/10/2026. Idioma: español argentino. Propietario: Augusto.

## Qué contiene

Este paquete es una especificación de implementación con el prototipo original incluido. **No es todavía la aplicación full-stack ni una implementación de las medidas de seguridad descritas.** El agente debe construirlas y verificarlas.

- `PROMPT-INICIAL.md`: texto para darle al agente de VS Code.
- `AGENTS.md`: reglas del proyecto y definición de terminado.
- `reference/`: HTML, CSS y JavaScript exactos del prototipo aprobado, sin credenciales ni datos financieros del usuario.
- `docs/01-PRODUCTO-Y-DISENO.md`: comportamiento, alcance y fidelidad visual.
- `docs/02-ARQUITECTURA-Y-DATOS.md`: arquitectura, reglas monetarias y API.
- `docs/03-SEGURIDAD.md`: controles concretos y pruebas negativas.
- `docs/04-VPS-Y-OPERACION.md`: despliegue, secretos, backups y recuperación.
- `docs/05-PLAN-Y-ACEPTACION.md`: orden de implementación y casos verificables.
- `docs/06-FUENTES.md`: documentación primaria para validar decisiones.
- `SHA256SUMS.txt`: integridad de los archivos del paquete.

## Cómo usarlo

1. Descomprimir en una carpeta nueva y abrirla en VS Code.
2. Dar al agente el contenido de `PROMPT-INICIAL.md` o pedirle que lo lea y ejecute.
3. Mantener `reference/` como referencia visual; el código nuevo vive en `src/`, `tests/` e `infra/`.
4. Revisar la primera pantalla contra el original antes de ampliar funciones.
5. Probar localmente con datos ficticios. Usar datos reales solo cuando los controles y la recuperación estén implementados y verificados.

El prototipo puede verse con un servidor estático local desde `reference/`. Sus registros se guardan en localStorage y no son aptos como almacenamiento final. La app nueva debe guardar en PostgreSQL. La importación del respaldo del prototipo se hará por archivo explícito: el dominio de la VPS no puede leer el localStorage del sitio anterior.

Sitio de referencia (privado): https://alvarez-finanzas.augustoalvarez10.chatgpt.site
No depender del acceso a ese sitio: los tres archivos originales están incluidos.

## Decisiones ya tomadas

- Diseño aprobado: oscuro con verde lima, tal como `reference/`; no rediseñar.
- React + TypeScript para la interfaz; ASP.NET Core + EF Core + PostgreSQL para el servidor.
- Despliegue Docker Compose detrás de Nginx en VPS propia.
- Un propietario, sin registro público. Credenciales propias de la app; no depender de ChatGPT.
- Cookies de sesión seguras y segundo factor TOTP, sin tokens en localStorage.
- ARS y USD separados, sin cotización automática.
- Sin conexión bancaria, scraping del home banking o lectura de correo en esta entrega.
- Importación del JSON del prototipo en primera versión; CSV/PDF bancarios quedan para una fase posterior explícita.

El dominio final, los accesos a la VPS y los secretos no se conocen ni se incluyen. Usar placeholders y terminar la implementación antes de pedir esos datos para un despliegue real.
