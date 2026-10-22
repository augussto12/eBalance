Quiero que implementes Álvarez Finanzas para mi VPS. Leé primero AGENTS.md y todos los documentos en docs/, y examiná reference/index.html, reference/style.css y reference/app.js.

La interfaz del prototipo ya está aprobada: reproducila con fidelidad en React + TypeScript. Conservá colores, tipografía, estructura, espaciados, navegación, textos y comportamiento adaptable a celular. No propongas otro diseño ni uses una plantilla genérica. Los archivos originales mandan sobre tu interpretación visual.

Construí una app funcional con ASP.NET Core, EF Core y PostgreSQL, Docker Compose y Nginx. Aplicá los requisitos de autenticación, segundo factor, sesiones revocables, autorización por propietario, CSRF, validación, secretos, logs y backups de docs/03 y docs/04. Elegí versiones estables soportadas compatibles entre sí, verificá documentación oficial y registrá las versiones fijadas. No uses previews.

No quiero únicamente un plan: implementá el código, migraciones, pruebas, configuración y documentación de operación. Trabajá por fases y verificá cada una. La especificación separa el alcance inicial de futuras mejoras. No conectes bancos ni agregues servicios de terceros para mis datos.

El prototipo contiene limitaciones deliberadamente documentadas: localStorage, cuotas con coma flotante, falta de autenticación y handlers inline. Conservá su apariencia y reemplazá esas implementaciones por las correctas. El dinero debe calcularse de manera exacta en el backend y no duplicar gastos al pagar tarjetas. Distinguí compromiso, gasto y movimiento de caja.

Usá datos ficticios solo en desarrollo o mediante una acción explícita; nunca precargues mis finanzas ni secretos. Para producción no debe existir registro público, cuenta/contraseña de ejemplo, ni modo de autenticación deshabilitada.

Mantené el registro de avance en docs/IMPLEMENTATION-STATUS.md con lo terminado, comandos realmente ejecutados, pruebas y bloqueos. Si algo no se probó, decilo. No declares la app segura por tener un checklist y no cierres la tarea con botones simulados o rutas sin implementar. No publiques ni modifiques mi VPS hasta tener la entrega local revisable y autorización para ese despliegue concreto. Nunca me pidas contraseñas en el chat; documentá su ingreso local mediante stdin/archivos de secretos protegidos.

Empezá inspeccionando el repositorio actual, preservá cambios existentes y avanzá con las decisiones ya especificadas. Solo preguntá si hay un bloqueo real o una decisión que altere el alcance.
