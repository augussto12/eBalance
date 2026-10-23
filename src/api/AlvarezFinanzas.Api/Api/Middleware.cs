using System.Diagnostics;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AlvarezFinanzas.Api;

public static class Middleware
{
    /// <summary>Correlation id por request (devuelto en X-Correlation-Id y en ProblemDetails).</summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        ctx.TraceIdentifier = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        // Al empezar la respuesta: el manejador de excepciones limpia los headers antes de escribir el 500.
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers["X-Correlation-Id"] = ctx.TraceIdentifier;
            return Task.CompletedTask;
        });
        await next();
    });

    /// <summary>Respuestas de API nunca cacheables y con cabeceras defensivas.</summary>
    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h.CacheControl = "no-store";
            h.Pragma = "no-cache";
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h.XFrameOptions = "DENY";
            h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        });
        await next();
    });

    /// <summary>
    /// Para POST/PUT/PATCH/DELETE: valida el token antiforgery del framework (header X-CSRF-TOKEN + cookie) y, como defensa
    /// adicional, Origin/Referer contra el origen público configurado. Si el navegador no envía ninguno de los dos, se
    /// acepta solo con el token CSRF válido (fallback documentado).
    /// </summary>
    public static IApplicationBuilder UseCsrfProtection(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var method = ctx.Request.Method;
        var unsafeMethod = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
        if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api"))
        {
            var expected = ctx.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value.PublicOrigin.TrimEnd('/');
            var origin = ctx.Request.Headers.Origin.ToString();
            var referer = ctx.Request.Headers.Referer.ToString();
            var originOk = origin.Length > 0
                ? string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase)
                : referer.Length == 0 || referer.StartsWith(expected + "/", StringComparison.OrdinalIgnoreCase);
            if (!originOk)
            {
                await Reject(ctx, "Origen no permitido.");
                return;
            }
            await AuthenticateCurrentAsync(ctx);
            var antiforgery = ctx.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(ctx);
            }
            catch (AntiforgeryValidationException ex)
            {
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Csrf").LogInformation("CSRF rechazado: {Reason}", ex.Message);
                await Reject(ctx, "Falta o no es válido el token de seguridad. Recargá la página.");
                return;
            }
        }
        await next();
    });

    /// <summary>
    /// Los endpoints anónimos (csrf, login, logout) no reciben ctx.User poblado: se autentica explícitamente con el esquema
    /// combinado para que generación y validación del token antiforgery usen la misma identidad.
    /// </summary>
    public static async Task AuthenticateCurrentAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true) return;
        var result = await ctx.AuthenticateAsync(AuthConstants.SmartScheme);
        if (result.Succeeded) ctx.User = result.Principal;
    }

    private static Task Reject(HttpContext ctx, string title)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Results.Problem(title: title, statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["code"] = "csrf" }).ExecuteAsync(ctx);
    }
}

/// <summary>Limpieza periódica: vistas previas de importación caducadas y sesiones viejas.</summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = clock.GetUtcNow();
                // Proceso de mantenimiento sobre todos los propietarios: sin sesión, el filtro global por owner se ignora explícitamente.
                await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteUpdateAsync(
                    Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.ImportBatches, [AppDbContext.OwnerFilter])
                        .Where(b => b.Status == Domain.ImportStatus.Preview && b.ExpiresAt < now),
                    s => s.SetProperty(b => b.Status, Domain.ImportStatus.Expired).SetProperty(b => b.NormalizedPayload, (string?)null), stoppingToken);
                var cutoff = now.AddDays(-30);
                await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync(
                    db.Sessions.Where(s => s.AbsoluteExpiresAt < cutoff), stoppingToken);
                await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteUpdateAsync(
                    db.Sessions.Where(s => s.ProtectedTicket != null && (s.AbsoluteExpiresAt < now || s.RevokedAt != null)),
                    s => s.SetProperty(x => x.ProtectedTicket, (byte[]?)null), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Falló la limpieza periódica: {Error}", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
