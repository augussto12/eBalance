using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using AlvarezFinanzas.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlvarezFinanzas.Infrastructure.Auth;

public static class AuthConstants
{
    public const string SessionScheme = "Session";
    public const string PreAuthScheme = "PreAuth";
    public const string SmartScheme = "Smart";
    public const string SessionCookie = "__Host-alvarez-session";
    public const string PreAuthCookie = "__Host-alvarez-preauth";
    public const string CsrfCookie = "__Host-alvarez-csrf";
    public const string CsrfHeader = "X-CSRF-TOKEN";

    public const string ClaimSessionId = "sid";
    public const string ClaimStage = "stage";
    public const string ClaimAmr = "amr";
    public const string ClaimElevatedUntil = "elev";
    public const string ClaimStamp = "stamp";

    public const string StageMfa = "mfa";
    public const string StageEnroll = "enroll";

    public const string ItemAbsoluteExpires = "abs_exp";
    public const string ItemElevatedUntil = "elev_until";

    public const string PolicyFinancial = "Financial";
    public const string PolicyPreAuth = "PreAuthOnly";
    public const string PolicyElevated = "Elevated";
}

/// <summary>
/// Sesiones del lado del servidor. La cookie solo lleva una referencia aleatoria de 256 bits (protegida por Data Protection);
/// en la base se guarda su SHA-256, el ticket protegido y los metadatos de expiración/revocación.
/// Cada request privado valida revocación, inactividad, máximo absoluto, usuario activo y security stamp.
/// </summary>
public sealed class DbTicketStore(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    IHttpContextAccessor http,
    IOptions<AppOptions> options,
    TimeProvider clock) : ITicketStore
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("AlvarezFinanzas.SessionTickets.v1");
    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromSeconds(60);

    public static byte[] HashKey(string key) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var kind = ticket.AuthenticationScheme == AuthConstants.PreAuthScheme ? SessionKind.PreAuth : SessionKind.Full;
        var userId = Guid.Parse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var absolute = kind == SessionKind.PreAuth
            ? now.AddMinutes(o.PreAuthMinutes)
            : now.AddHours(o.SessionAbsoluteHours);
        // La rotación por elevación nunca extiende el máximo absoluto original.
        if (ticket.Properties.Items.TryGetValue(AuthConstants.ItemAbsoluteExpires, out var absText)
            && DateTimeOffset.TryParse(absText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var prevAbs)
            && prevAbs < absolute)
        {
            absolute = prevAbs;
        }
        DateTimeOffset? elevated = null;
        if (ticket.Properties.Items.TryGetValue(AuthConstants.ItemElevatedUntil, out var elevText)
            && DateTimeOffset.TryParse(elevText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var elev))
        {
            elevated = elev;
        }
        ticket.Properties.Items.Remove(AuthConstants.ItemAbsoluteExpires);
        ticket.Properties.Items.Remove(AuthConstants.ItemElevatedUntil);

        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        var ctx = http.HttpContext;
        var session = new UserSession
        {
            KeyHash = HashKey(key),
            UserId = userId,
            Kind = kind,
            ProtectedTicket = _protector.Protect(TicketSerializer.Default.Serialize(ticket)),
            CreatedAt = now,
            LastSeenAt = now,
            AbsoluteExpiresAt = absolute,
            ElevatedUntil = elevated,
            UserAgent = Truncate(ctx?.Request.Headers.UserAgent.ToString(), 160),
            IpPrefix = IpMinimizer.Prefix(ctx?.Connection.RemoteIpAddress),
        };
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return key;
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var hash = HashKey(key);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Sessions
            .Where(s => s.KeyHash == hash)
            .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { Session = s, u.IsActive, u.SecurityStamp })
            .SingleOrDefaultAsync();
        if (row is null || row.Session.RevokedAt is not null || row.Session.ProtectedTicket is null) return null;
        var s = row.Session;
        if (now >= s.AbsoluteExpiresAt) return null;
        if (s.Kind == SessionKind.Full && now - s.LastSeenAt >= TimeSpan.FromMinutes(o.SessionIdleMinutes)) return null;
        if (!row.IsActive) return null;

        AuthenticationTicket? ticket;
        try
        {
            ticket = TicketSerializer.Default.Deserialize(_protector.Unprotect(s.ProtectedTicket));
        }
        catch (CryptographicException)
        {
            return null;
        }
        if (ticket is null) return null;
        if (ticket.Principal.FindFirstValue(AuthConstants.ClaimStamp) != row.SecurityStamp) return null;

        if (now - s.LastSeenAt >= LastSeenWriteInterval)
        {
            await db.Sessions.Where(x => x.Id == s.Id && x.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, now));
        }

        var identity = (ClaimsIdentity)ticket.Principal.Identity!;
        identity.AddClaim(new Claim(AuthConstants.ClaimSessionId, s.Id.ToString()));
        if (s.ElevatedUntil is { } elev && elev > now)
        {
            identity.AddClaim(new Claim(AuthConstants.ClaimElevatedUntil, elev.ToString("O", CultureInfo.InvariantCulture)));
        }
        var idleLimit = s.Kind == SessionKind.Full ? now.AddMinutes(o.SessionIdleMinutes) : s.AbsoluteExpiresAt;
        ticket.Properties.ExpiresUtc = idleLimit < s.AbsoluteExpiresAt ? idleLimit : s.AbsoluteExpiresAt;
        ticket.Properties.IsPersistent = false;
        return ticket;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var hash = HashKey(key);
        var blob = _protector.Protect(TicketSerializer.Default.Serialize(ticket));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Sessions.Where(s => s.KeyHash == hash && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedTicket, blob));
    }

    public async Task RemoveAsync(string key)
    {
        var hash = HashKey(key);
        var now = clock.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Sessions.Where(s => s.KeyHash == hash && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.RevokedReason, "logout")
                .SetProperty(x => x.ProtectedTicket, (byte[]?)null));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];
}

/// <summary>Configura las cookies de sesión con el store de servidor y respuestas 401/403 JSON (nunca HTML de login).</summary>
public sealed class SessionCookieConfigurator(DbTicketStore store, IOptions<AppOptions> options)
    : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(CookieAuthenticationOptions o) => Configure(Options.DefaultName, o);

    public void Configure(string? name, CookieAuthenticationOptions o)
    {
        if (name is not (AuthConstants.SessionScheme or AuthConstants.PreAuthScheme)) return;
        o.SessionStore = store;
        // Prefijo __Host-: exige Secure, Path=/ y sin Domain. Requiere HTTPS también en desarrollo y pruebas.
        o.Cookie.Name = name == AuthConstants.SessionScheme ? AuthConstants.SessionCookie : AuthConstants.PreAuthCookie;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
        o.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
        o.Cookie.Path = "/";
        o.Cookie.Domain = null;
        o.Cookie.IsEssential = true;
        o.SlidingExpiration = false;
        o.ExpireTimeSpan = name == AuthConstants.SessionScheme
            ? TimeSpan.FromMinutes(options.Value.SessionIdleMinutes)
            : TimeSpan.FromMinutes(options.Value.PreAuthMinutes);
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        o.Events.OnRedirectToLogout = ctx => { ctx.Response.StatusCode = StatusCodes.Status204NoContent; return Task.CompletedTask; };
        o.Events.OnRedirectToReturnUrl = ctx => { ctx.Response.StatusCode = StatusCodes.Status204NoContent; return Task.CompletedTask; };
    }
}

/// <summary>
/// Rotación de sesión dentro de un request ya autenticado. El handler de cookies, si autenticó la sesión en este
/// request, reutiliza su clave en SignIn (RenewAsync). Para emitir una referencia nueva se guarda un ticket nuevo y se
/// escribe la cookie con el mismo formato y opciones que el handler.
/// </summary>
public static class SessionRotation
{
    private const string SessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

    public static async Task IssueNewSessionAsync(HttpContext ctx, ClaimsPrincipal principal, AuthenticationProperties props)
    {
        var options = ctx.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthConstants.SessionScheme);
        var ticket = new AuthenticationTicket(principal, props, AuthConstants.SessionScheme);
        var key = await options.SessionStore!.StoreAsync(ticket, ctx, ctx.RequestAborted);
        var reference = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionIdClaim, key, ClaimValueTypes.String, options.ClaimsIssuer)], options.ClaimsIssuer)),
            null, AuthConstants.SessionScheme);
        var cookieOptions = options.Cookie.Build(ctx);
        cookieOptions.Expires = null;
        options.CookieManager.AppendResponseCookie(ctx, options.Cookie.Name!, options.TicketDataFormat.Protect(reference), cookieOptions);
    }
}
