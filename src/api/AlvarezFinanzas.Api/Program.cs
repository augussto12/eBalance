using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AlvarezFinanzas.Api;
using AlvarezFinanzas.Application;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlvarezFinanzas;

public partial class Program
{
    /// <summary>
    /// PBKDF2-HMAC-SHA512 (formato v3 de Identity). OWASP recomienda 210.000 iteraciones para esta variante. Coste medido
    /// (PasswordHashingTests): unos 156–159 ms por verificación en 4 vCPU Xeon a 2,1 GHz con .NET 10.0.13. Los hashes con una
    /// política anterior se re-hashean al iniciar sesión (UserManager.CheckPasswordAsync).
    /// </summary>
    public const int PasswordIterations = 210_000;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && Cli.IsCommand(args[0]))
        {
            return await Cli.RunAsync(args);
        }
        var app = Build(args);
        // Si el key ring no corresponde a la base, KeyRingGuard aborta el arranque: el proceso termina con error y el
        // motivo queda en el log (Critical «Key ring rechazado»), nunca una API con claves nuevas.
        await app.RunAsync();
        return 0;
    }

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables("ALVAREZ_");
        configure?.Invoke(builder);
        ConfigureServices(builder);
        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;
        var env = builder.Environment;
        services.AddOptions<AppOptions>().Bind(config.GetSection("App")).Validate(o => o.PublicOrigin.StartsWith("https://", StringComparison.Ordinal),
            "App:PublicOrigin debe ser https://…").ValidateOnStart();
        var options = config.GetSection("App").Get<AppOptions>() ?? new AppOptions();

        builder.Logging.ClearProviders();
        if (env.IsProduction()) builder.Logging.AddJsonConsole(o => o.IncludeScopes = false);
        else builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
        });

        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
        {
            c.ProblemDetails.Extensions.Remove("exception");
            c.ProblemDetails.Extensions["correlationId"] = c.HttpContext.TraceIdentifier;
        });

        // Propietario de la sesión para los filtros globales por owner_id (defensa adicional a los filtros explícitos).
        services.AddScoped<ICurrentOwner, SessionOwner>();
        services.AddDbContext<AppDbContext>(o =>
        {
            o.UseNpgsql(SecretFiles.BuildConnectionString(config, "App"), npg => npg.CommandTimeout(15))
             .UseSnakeCaseNamingConvention();
        });

        // Data Protection: key ring persistido y cifrado explícitamente con certificado. Fallar cerrado en producción,
        // también si el key ring no corresponde a la base (KeyRingGuard verifica el centinela antes de que se cree ninguna clave).
        services.AddDataProtection().ConfigureKeyRing(options, env.IsProduction());
        services.AddSingleton<ReadOnlyKeyRing>();
        services.AddSingleton<KeyRingGuard>();
        services.AddHostedService(sp => sp.GetRequiredService<KeyRingGuard>());

        services.AddIdentityCore<AppUser>(o =>
            {
                o.Password.RequiredLength = AuthEndpoints.PasswordMinLength;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 1;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 10;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@";
                o.User.RequireUniqueEmail = false;
            })
            .AddEntityFrameworkStores<AppDbContext>();
        services.Configure<PasswordHasherOptions>(o =>
        {
            o.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            o.IterationCount = PasswordIterations;
        });

        services.AddSingleton<DbTicketStore>();
        services.ConfigureOptions<SessionCookieConfigurator>();
        services.AddAuthentication(AuthConstants.SmartScheme)
            .AddPolicyScheme(AuthConstants.SmartScheme, null, o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Cookies.ContainsKey(AuthConstants.SessionCookie) ? AuthConstants.SessionScheme : AuthConstants.PreAuthScheme)
            .AddCookie(AuthConstants.SessionScheme)
            .AddCookie(AuthConstants.PreAuthScheme);

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthConstants.PolicyFinancial, p => p
                .AddAuthenticationSchemes(AuthConstants.SessionScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(AuthConstants.ClaimAmr, "mfa"))
            .AddPolicy(AuthConstants.PolicyPreAuth, p => p
                .AddAuthenticationSchemes(AuthConstants.PreAuthScheme)
                .RequireAuthenticatedUser())
            .AddPolicy(AuthConstants.PolicyElevated, p => p
                .AddAuthenticationSchemes(AuthConstants.SessionScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(AuthConstants.ClaimAmr, "mfa")
                .RequireAssertion(ctx =>
                {
                    var value = ctx.User.FindFirst(AuthConstants.ClaimElevatedUntil)?.Value;
                    return value is not null
                           && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var until)
                           && until > DateTimeOffset.UtcNow;
                }))
            .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AuthConstants.SessionScheme)
                .RequireAuthenticatedUser().RequireClaim(AuthConstants.ClaimAmr, "mfa").Build())
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AuthConstants.SessionScheme)
                .RequireAuthenticatedUser().RequireClaim(AuthConstants.ClaimAmr, "mfa").Build());

        services.AddAntiforgery(o =>
        {
            o.HeaderName = AuthConstants.CsrfHeader;
            o.Cookie.Name = AuthConstants.CsrfCookie;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.HttpOnly = true;
            o.Cookie.Path = "/";
            o.SuppressXFrameOptionsHeader = true;
        });

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            // Solo proxies explícitos. Nunca confiar en cualquier red.
            foreach (var item in options.TrustedProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (item.Contains('/')) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(item));
                else o.KnownProxies.Add(IPAddress.Parse(item));
            }
        });

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
        });
        services.AddRequestTimeouts(o => o.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(30) });

        services.ConfigureHttpJsonOptions(o =>
        {
            // Allowlist estricta: propiedades desconocidas → 400 (sin mass assignment).
            o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.MaxDepth = 16;
        });

        services.AddSingleton<AttemptLimiter>();
        services.AddSingleton<TotpService>();
        services.AddSingleton<RecoveryCodeService>();
        services.AddScoped<AuditLog>();
        services.AddScoped<ReportService>();
        services.AddScoped<BalanceService>();
        services.AddHostedService<MaintenanceService>();
        services.AddHealthChecks();
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseCorrelationId();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseApiSecurityHeaders();
        app.UseRateLimiter();
        app.UseRouting();
        app.UseRequestTimeouts();
        app.UseAuthentication();
        app.UseCsrfProtection();
        app.UseAuthorization();

        app.MapGet("/api/health", () => TypedResults.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapGet("/api/health/ready", async (AppDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503)).AllowAnonymous();

        var api = app.MapGroup("/api/v1");
        AuthEndpoints.Map(api);
        var data = api.MapGroup("").RequireAuthorization(AuthConstants.PolicyFinancial);
        CatalogEndpoints.Map(data);
        EntryEndpoints.Map(data);
        PlanEndpoints.Map(data);
        CashEndpoints.Map(data);
        ReportEndpoints.Map(data);
        ImportExportEndpoints.Map(data);
        var importOptions = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
        DevEndpoints.Map(data, app.Environment, importOptions);

        // Cualquier otra ruta /api → 404 JSON (nunca el HTML del SPA).
        app.Map("/api/{**rest}", () => Problems.NotFound()).AllowAnonymous();
    }
}
