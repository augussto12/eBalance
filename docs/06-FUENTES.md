# Fuentes primarias y mantenimiento

Revisadas para este handoff el 20/10/2026. El agente debe verificar versiones concretas antes de implementar. No copiar ejemplos de versiones antiguas sin adaptarlos. Las decisiones de arquitectura y parámetros de negocio del paquete son propuestas específicas para esta app, no requisitos universales de estas fuentes.

- Microsoft, antiforgery ASP.NET Core: https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery
  Base para validar CSRF con cookie auth; asegurar cookies explícitamente en producción.
- Microsoft, configuración de Data Protection: https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview
  Persistencia, permisos y cifrado explícito del key ring. Guardar claves en filesystem no cifra automáticamente su contenido.
- OWASP Password Storage: https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html
  Algoritmos/coste de hash; comprobar variante exacta de PBKDF2 y política vigente.
- OWASP Session Management: https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html
  Identificadores de sesión, cookies, expiración y revocación.
- OWASP Multifactor Authentication: https://cheatsheetseries.owasp.org/cheatsheets/Multifactor_Authentication_Cheat_Sheet.html
  Protección de factores y recuperación.
- OWASP Secrets Management: https://cheatsheetseries.owasp.org/cheatsheets/Secrets_Management_Cheat_Sheet.html
  Ciclo de vida y separación de secretos.

No se ha realizado auditoría de seguridad del futuro backend: aún debe implementarse. Los fuentes del prototipo sirven para diseño y migración, no como ejemplo seguro de producción.
