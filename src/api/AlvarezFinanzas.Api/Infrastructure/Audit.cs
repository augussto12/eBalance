using System.Security.Claims;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure.Auth;

namespace AlvarezFinanzas.Infrastructure;

/// <summary>
/// Auditoría con actor, acción, recurso, resultado, hora y correlation id. Nunca importes, conceptos ni secretos.
/// </summary>
public sealed class AuditLog(IHttpContextAccessor http, TimeProvider clock, ILogger<AuditLog> logger)
{
    /// <summary>Agrega el evento al contexto: se guarda en la misma transacción que la operación.</summary>
    public void Add(AppDbContext db, string action, string result, string? resourceType = null, object? resourceId = null, Guid? actorId = null)
    {
        var ctx = http.HttpContext;
        actorId ??= ctx?.User.Identity?.IsAuthenticated == true && Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;
        db.AuditEvents.Add(new AuditEvent
        {
            ActorId = actorId,
            Action = action,
            Result = result,
            ResourceType = resourceType,
            ResourceId = resourceId?.ToString(),
            OccurredAt = clock.GetUtcNow(),
            CorrelationId = ctx?.TraceIdentifier,
            IpPrefix = IpMinimizer.Prefix(ctx?.Connection.RemoteIpAddress),
        });
        logger.LogInformation("audit {Action} {Result} {ResourceType} {ResourceId} actor={ActorId}", action, result, resourceType, resourceId, actorId);
    }

    /// <summary>Guarda el evento de inmediato (p. ej. intentos fallidos de login).</summary>
    public async Task WriteAsync(AppDbContext db, string action, string result, string? resourceType = null, object? resourceId = null, Guid? actorId = null)
    {
        Add(db, action, result, resourceType, resourceId, actorId);
        await db.SaveChangesAsync();
    }
}
