using System.Globalization;
using System.Security.Claims;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlvarezFinanzas.Api;

public static class AuthEndpoints
{
    public const int PasswordMinLength = 15;
    public const int PasswordMaxLength = 128;
    private const string GenericLoginError = "Usuario o contraseña incorrectos.";
    private const string GenericCodeError = "El código no es válido o ya fue usado.";

    public sealed record LoginRequest(string? Username, string? Password);
    public sealed record MfaVerifyRequest(string? Code, string? RecoveryCode);
    public sealed record CodeRequest(string? Code);
    public sealed record ReauthRequest(string? Password, string? Code);
    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
    public sealed record ProfileRequest(string? DisplayName, long? Version);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapGet("/csrf", async (HttpContext ctx, IAntiforgery af) =>
        {
            // El token queda ligado a la identidad actual (anónima, preautenticada o sesión completa).
            await Middleware.AuthenticateCurrentAsync(ctx);
            var tokens = af.GetAndStoreTokens(ctx);
            return TypedResults.Ok(new { token = tokens.RequestToken });
        }).AllowAnonymous();

        g.MapGet("/me", Me).AllowAnonymous();
        g.MapPost("/login", Login).AllowAnonymous();
        g.MapPost("/mfa/verify", VerifyMfa).RequireAuthorization(AuthConstants.PolicyPreAuth);
        g.MapPost("/mfa/enroll/start", EnrollStart).RequireAuthorization(AuthConstants.PolicyPreAuth);
        g.MapPost("/mfa/enroll/confirm", EnrollConfirm).RequireAuthorization(AuthConstants.PolicyPreAuth);
        g.MapPost("/logout", Logout).AllowAnonymous();
        g.MapPost("/logout-all", LogoutAll);
        g.MapGet("/sessions", Sessions);
        g.MapDelete("/sessions/{id:guid}", RevokeSession);
        g.MapPost("/reauth", Reauth);
        g.MapPost("/password/change", ChangePassword).RequireAuthorization(AuthConstants.PolicyElevated);
        g.MapPost("/recovery-codes/regenerate", RegenerateCodes).RequireAuthorization(AuthConstants.PolicyElevated);
        g.MapPost("/mfa/reset", ResetMfa).RequireAuthorization(AuthConstants.PolicyElevated);
        g.MapPatch("/me", UpdateProfile);
    }

    private static ClaimsPrincipal BuildPrincipal(AppUser user, string scheme, string? stage)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName!),
            new(AuthConstants.ClaimStamp, user.SecurityStamp ?? ""),
        };
        if (stage is not null) claims.Add(new Claim(AuthConstants.ClaimStage, stage));
        if (scheme == AuthConstants.SessionScheme) claims.Add(new Claim(AuthConstants.ClaimAmr, "mfa"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    private static string Initials(AppUser u)
    {
        var name = string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName ?? "" : u.DisplayName;
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length >= 2 ? $"{parts[0][0]}{parts[^1][0]}" : name.Length > 0 ? name[..Math.Min(2, name.Length)] : "?";
        return initials.ToUpper(CultureInfo.GetCultureInfo("es-AR"));
    }

    private static async Task<IResult> Me(HttpContext ctx, AppDbContext db, UserManager<AppUser> users,
        RecoveryCodeService recovery, IOptions<AppOptions> options, IHostEnvironment env, CancellationToken ct)
    {
        var session = await ctx.AuthenticateAsync(AuthConstants.SessionScheme);
        if (session.Succeeded && session.Principal.HasClaim(AuthConstants.ClaimAmr, "mfa"))
        {
            var user = await users.FindByIdAsync(session.Principal.UserId().ToString());
            if (user is null) return TypedResults.Ok(new { state = "anonymous" });
            var elevated = session.Principal.FindFirstValue(AuthConstants.ClaimElevatedUntil);
            return TypedResults.Ok(new
            {
                state = "authenticated",
                user = new
                {
                    id = user.Id,
                    username = user.UserName,
                    displayName = user.DisplayName,
                    initials = Initials(user),
                    timeZone = user.TimeZoneId,
                    version = user.ProfileVersion,
                },
                recoveryCodesRemaining = await recovery.RemainingAsync(db, user.Id, ct),
                elevatedUntil = elevated,
                demoEnabled = env.IsDevelopment() && options.Value.EnableDemoData,
            });
        }
        var pre = await ctx.AuthenticateAsync(AuthConstants.PreAuthScheme);
        if (pre.Succeeded)
        {
            var stage = pre.Principal.FindFirstValue(AuthConstants.ClaimStage);
            return TypedResults.Ok(new { state = stage == AuthConstants.StageEnroll ? "enroll_required" : "mfa_required" });
        }
        return TypedResults.Ok(new { state = "anonymous" });
    }

    private static async Task<IResult> Login(LoginRequest req, HttpContext ctx, AppDbContext db, UserManager<AppUser> users,
        AttemptLimiter limiter, AuditLog audit, IPasswordHasher<AppUser> hasher)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // El límite por IP se evalúa antes de calcular PBKDF2 para acotar CPU.
        var ipLimit = limiter.LoginByIp(ip);
        if (!ipLimit.IsAllowed) return Problems.TooMany(ipLimit.RetryAfter, ctx);

        var username = req.Username?.Trim() ?? "";
        var password = req.Password ?? "";
        if (username.Length is 0 or > 64 || password.Length is 0 or > PasswordMaxLength)
        {
            return TypedResults.Problem(title: GenericLoginError, statusCode: StatusCodes.Status401Unauthorized);
        }
        var acctLimit = limiter.LoginByAccount(limiter.AccountKey(username));
        if (!acctLimit.IsAllowed) return Problems.TooMany(acctLimit.RetryAfter, ctx);

        var user = await users.FindByNameAsync(username);
        if (user is null || !user.IsActive)
        {
            // Igualar costo de verificación para no revelar si la cuenta existe.
            hasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, password);
            await audit.WriteAsync(db, "auth.login", "failure");
            return TypedResults.Problem(title: GenericLoginError, statusCode: StatusCodes.Status401Unauthorized);
        }
        if (await users.IsLockedOutAsync(user))
        {
            hasher.VerifyHashedPassword(user, DummyHash.Value, password);
            await audit.WriteAsync(db, "auth.login", "locked_out", actorId: user.Id);
            return TypedResults.Problem(title: GenericLoginError, statusCode: StatusCodes.Status401Unauthorized);
        }
        // CheckPasswordAsync re-hashea automáticamente si cambió la política de iteraciones.
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            await audit.WriteAsync(db, "auth.login", "failure", actorId: user.Id);
            return TypedResults.Problem(title: GenericLoginError, statusCode: StatusCodes.Status401Unauthorized);
        }
        await users.ResetAccessFailedCountAsync(user);

        await ctx.SignOutAsync(AuthConstants.SessionScheme);
        await ctx.SignOutAsync(AuthConstants.PreAuthScheme);
        var stage = user.TwoFactorEnabled && user.TotpSecretProtected is not null ? AuthConstants.StageMfa : AuthConstants.StageEnroll;
        await ctx.SignInAsync(AuthConstants.PreAuthScheme, BuildPrincipal(user, AuthConstants.PreAuthScheme, stage),
            new AuthenticationProperties { IsPersistent = false });
        await audit.WriteAsync(db, "auth.login.password", "success", actorId: user.Id);
        return TypedResults.Ok(new { state = stage == AuthConstants.StageEnroll ? "enroll_required" : "mfa_required" });
    }

    private static async Task<IResult> VerifyMfa(MfaVerifyRequest req, HttpContext ctx, AppDbContext db, UserManager<AppUser> users,
        AttemptLimiter limiter, AuditLog audit, TotpService totp, RecoveryCodeService recovery, TimeProvider clock, CancellationToken ct)
    {
        if (ctx.User.FindFirstValue(AuthConstants.ClaimStage) != AuthConstants.StageMfa) return Problems.Forbidden("Paso de autenticación no válido.");
        var userId = ctx.User.UserId();
        var limit = limiter.Mfa(userId.ToString());
        if (!limit.IsAllowed) return Problems.TooMany(limit.RetryAfter, ctx);

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.TotpSecretProtected is null) return TypedResults.Unauthorized();
        if (await users.IsLockedOutAsync(user))
        {
            await audit.WriteAsync(db, "auth.mfa", "locked_out", actorId: userId);
            return TypedResults.Problem(title: GenericCodeError, statusCode: StatusCodes.Status401Unauthorized);
        }

        var usedRecovery = false;
        bool ok;
        if (!string.IsNullOrWhiteSpace(req.RecoveryCode))
        {
            ok = req.RecoveryCode.Length <= 40 && await recovery.TryConsumeAsync(db, userId, req.RecoveryCode, ct);
            usedRecovery = ok;
        }
        else
        {
            ok = req.Code is not null && await totp.TryConsumeAsync(db, userId, user.TotpSecretProtected, req.Code.Trim(), ct);
        }
        if (!ok)
        {
            await users.AccessFailedAsync(user);
            await audit.WriteAsync(db, "auth.mfa", "failure", actorId: userId);
            return TypedResults.Problem(title: GenericCodeError, statusCode: StatusCodes.Status401Unauthorized);
        }
        await users.ResetAccessFailedCountAsync(user);

        if (usedRecovery)
        {
            // Recuperar la cuenta revoca todas las demás sesiones.
            await RevokeAllAsync(db, userId, clock, "recovery_code");
        }
        await ctx.SignOutAsync(AuthConstants.PreAuthScheme);
        await ctx.SignInAsync(AuthConstants.SessionScheme, BuildPrincipal(user, AuthConstants.SessionScheme, null),
            new AuthenticationProperties { IsPersistent = false });
        await audit.WriteAsync(db, usedRecovery ? "auth.recovery_code" : "auth.mfa", "success", actorId: userId);
        return TypedResults.Ok(new
        {
            state = "authenticated",
            usedRecoveryCode = usedRecovery,
            recoveryCodesRemaining = await recovery.RemainingAsync(db, userId, ct),
        });
    }

    private static async Task<IResult> EnrollStart(HttpContext ctx, AppDbContext db, UserManager<AppUser> users, AttemptLimiter limiter,
        TotpService totp, AuditLog audit, TimeProvider clock)
    {
        if (ctx.User.FindFirstValue(AuthConstants.ClaimStage) != AuthConstants.StageEnroll) return Problems.Forbidden("Paso de autenticación no válido.");
        var userId = ctx.User.UserId();
        var limit = limiter.Mfa("enroll:" + userId);
        if (!limit.IsAllowed) return Problems.TooMany(limit.RetryAfter, ctx);
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive) return TypedResults.Unauthorized();

        user.TotpPendingSecretProtected = totp.NewProtectedSecret(out var base32);
        user.TotpPendingCreatedAt = clock.GetUtcNow();
        user.ProfileVersion++;
        await db.SaveChangesAsync();
        await audit.WriteAsync(db, "auth.mfa.enroll_start", "success", actorId: userId);
        return TypedResults.Ok(new { otpauthUri = TotpService.OtpAuthUri(user.UserName!, base32), secret = base32 });
    }

    private static async Task<IResult> EnrollConfirm(CodeRequest req, HttpContext ctx, AppDbContext db, UserManager<AppUser> users,
        AttemptLimiter limiter, TotpService totp, RecoveryCodeService recovery, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        if (ctx.User.FindFirstValue(AuthConstants.ClaimStage) != AuthConstants.StageEnroll) return Problems.Forbidden("Paso de autenticación no válido.");
        var userId = ctx.User.UserId();
        var limit = limiter.Mfa(userId.ToString());
        if (!limit.IsAllowed) return Problems.TooMany(limit.RetryAfter, ctx);
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive) return TypedResults.Unauthorized();
        if (user.TotpPendingSecretProtected is null || user.TotpPendingCreatedAt is null
            || clock.GetUtcNow() - user.TotpPendingCreatedAt > TimeSpan.FromMinutes(10))
        {
            return Problems.Unprocessable("La configuración caducó. Generá un código QR nuevo.");
        }
        var pending = user.TotpPendingSecretProtected;
        if (req.Code is null || !await totp.TryConsumeAsync(db, userId, pending, req.Code.Trim(), ct))
        {
            await users.AccessFailedAsync(user);
            await audit.WriteAsync(db, "auth.mfa.enroll_confirm", "failure", actorId: userId);
            return TypedResults.Problem(title: GenericCodeError, statusCode: StatusCodes.Status401Unauthorized);
        }
        await db.Entry(user).ReloadAsync(ct);
        user.TotpSecretProtected = pending;
        user.TotpPendingSecretProtected = null;
        user.TotpPendingCreatedAt = null;
        user.TwoFactorEnabled = true;
        user.ProfileVersion++;
        var update = await users.UpdateAsync(user);
        if (!update.Succeeded) return Problems.Conflict("No se pudo activar el segundo factor. Probá de nuevo.");
        await users.UpdateSecurityStampAsync(user);
        var codes = await recovery.RegenerateAsync(db, userId, ct);

        await ctx.SignOutAsync(AuthConstants.PreAuthScheme);
        await RevokeAllAsync(db, userId, clock, "mfa_enrolled");
        await ctx.SignInAsync(AuthConstants.SessionScheme, BuildPrincipal(user, AuthConstants.SessionScheme, null),
            new AuthenticationProperties { IsPersistent = false });
        await audit.WriteAsync(db, "auth.mfa.enroll_confirm", "success", actorId: userId);
        return TypedResults.Ok(new { state = "authenticated", recoveryCodes = codes });
    }

    private static async Task<IResult> Logout(HttpContext ctx, AppDbContext db, AuditLog audit)
    {
        var session = await ctx.AuthenticateAsync(AuthConstants.SessionScheme);
        await ctx.SignOutAsync(AuthConstants.SessionScheme);
        await ctx.SignOutAsync(AuthConstants.PreAuthScheme);
        if (session.Succeeded) await audit.WriteAsync(db, "auth.logout", "success", actorId: session.Principal.UserId());
        return TypedResults.NoContent();
    }

    private static async Task<IResult> LogoutAll(HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock)
    {
        var userId = ctx.User.UserId();
        await RevokeAllAsync(db, userId, clock, "logout_all");
        await ctx.SignOutAsync(AuthConstants.SessionScheme);
        await audit.WriteAsync(db, "auth.logout_all", "success", actorId: userId);
        return TypedResults.NoContent();
    }

    internal static Task<int> RevokeAllAsync(AppDbContext db, Guid userId, TimeProvider clock, string reason)
    {
        var now = clock.GetUtcNow();
        return db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.RevokedReason, reason)
                .SetProperty(x => x.ProtectedTicket, (byte[]?)null));
    }

    private static async Task<IResult> Sessions(HttpContext ctx, AppDbContext db, TimeProvider clock, IOptions<AppOptions> options)
    {
        var userId = ctx.User.UserId();
        var current = ctx.User.SessionId();
        var now = clock.GetUtcNow();
        var idleCutoff = now.AddMinutes(-options.Value.SessionIdleMinutes);
        var list = await db.Sessions
            .Where(s => s.UserId == userId && s.Kind == SessionKind.Full && s.RevokedAt == null
                        && s.AbsoluteExpiresAt > now && s.LastSeenAt > idleCutoff)
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new { s.Id, s.CreatedAt, s.LastSeenAt, s.AbsoluteExpiresAt, s.UserAgent, s.IpPrefix })
            .ToListAsync();
        return TypedResults.Ok(list.Select(s => new
        {
            id = s.Id, createdAt = s.CreatedAt, lastSeenAt = s.LastSeenAt, expiresAt = s.AbsoluteExpiresAt,
            userAgent = s.UserAgent, ipPrefix = s.IpPrefix, current = s.Id == current,
        }));
    }

    private static async Task<IResult> RevokeSession(Guid id, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock)
    {
        var userId = ctx.User.UserId();
        var now = clock.GetUtcNow();
        var n = await db.Sessions.Where(s => s.Id == id && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.RevokedReason, "revoked")
                .SetProperty(x => x.ProtectedTicket, (byte[]?)null));
        if (n == 0) return Problems.NotFound();
        await audit.WriteAsync(db, "auth.session.revoke", "success", "session", id);
        return TypedResults.NoContent();
    }

    /// <summary>Reautenticación reciente (contraseña + TOTP). Rota la sesión y la eleva por unos minutos.</summary>
    private static async Task<IResult> Reauth(ReauthRequest req, HttpContext ctx, AppDbContext db, UserManager<AppUser> users,
        AttemptLimiter limiter, TotpService totp, AuditLog audit, TimeProvider clock, IOptions<AppOptions> options, CancellationToken ct)
    {
        var userId = ctx.User.UserId();
        var limit = limiter.Mfa("reauth:" + userId);
        if (!limit.IsAllowed) return Problems.TooMany(limit.RetryAfter, ctx);
        var user = await users.FindByIdAsync(userId.ToString());
        if (user?.TotpSecretProtected is null) return TypedResults.Unauthorized();
        if (await users.IsLockedOutAsync(user)) return TypedResults.Problem(title: "Contraseña o código incorrectos.", statusCode: StatusCodes.Status401Unauthorized);
        var passwordOk = (req.Password?.Length ?? 0) is > 0 and <= PasswordMaxLength && await users.CheckPasswordAsync(user, req.Password!);
        var codeOk = passwordOk && req.Code is not null && await totp.TryConsumeAsync(db, userId, user.TotpSecretProtected, req.Code.Trim(), ct);
        if (!passwordOk || !codeOk)
        {
            await users.AccessFailedAsync(user);
            await audit.WriteAsync(db, "auth.reauth", "failure", actorId: userId);
            return TypedResults.Problem(title: "Contraseña o código incorrectos.", statusCode: StatusCodes.Status401Unauthorized);
        }
        await db.Entry(user).ReloadAsync(ct);
        var sid = ctx.User.SessionId();
        var currentSession = await db.Sessions.SingleAsync(s => s.Id == sid, ct);
        var now = clock.GetUtcNow();
        var elevatedUntil = now.AddMinutes(options.Value.ElevationMinutes);
        await db.Sessions.Where(s => s.Id == sid).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.RevokedAt, now)
            .SetProperty(x => x.RevokedReason, "rotated")
            .SetProperty(x => x.ProtectedTicket, (byte[]?)null), ct);
        var props = new AuthenticationProperties { IsPersistent = false };
        props.Items[AuthConstants.ItemAbsoluteExpires] = currentSession.AbsoluteExpiresAt.ToString("O", CultureInfo.InvariantCulture);
        props.Items[AuthConstants.ItemElevatedUntil] = elevatedUntil.ToString("O", CultureInfo.InvariantCulture);
        // Rotación: la referencia anterior queda revocada y se emite una nueva.
        await SessionRotation.IssueNewSessionAsync(ctx, BuildPrincipal(user, AuthConstants.SessionScheme, null), props);
        await audit.WriteAsync(db, "auth.reauth", "success", actorId: userId);
        return TypedResults.Ok(new { elevatedUntil });
    }

    private static async Task<IResult> ChangePassword(ChangePasswordRequest req, HttpContext ctx, AppDbContext db,
        UserManager<AppUser> users, AuditLog audit, TimeProvider clock)
    {
        var v = new Validation();
        var newPassword = req.NewPassword ?? "";
        if (newPassword.Length < PasswordMinLength) v.Add("newPassword", $"La contraseña debe tener al menos {PasswordMinLength} caracteres.");
        if (newPassword.Length > PasswordMaxLength) v.Add("newPassword", $"La contraseña admite hasta {PasswordMaxLength} caracteres.");
        if (string.IsNullOrEmpty(req.CurrentPassword) || req.CurrentPassword.Length > PasswordMaxLength) v.Add("currentPassword", "Ingresá tu contraseña actual.");
        if (!v.IsValid) return v.Problem();
        var userId = ctx.User.UserId();
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return TypedResults.Unauthorized();
        var result = await users.ChangePasswordAsync(user, req.CurrentPassword!, newPassword);
        if (!result.Succeeded)
        {
            await audit.WriteAsync(db, "auth.password.change", "failure", actorId: userId);
            var mismatch = result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch));
            v.Add(mismatch ? "currentPassword" : "newPassword", mismatch ? "La contraseña actual no es correcta." : "La contraseña nueva no cumple la política.");
            return v.Problem();
        }
        // Cambiar contraseña revoca todas las sesiones (incluida la actual).
        await RevokeAllAsync(db, userId, clock, "password_changed");
        await ctx.SignOutAsync(AuthConstants.SessionScheme);
        await audit.WriteAsync(db, "auth.password.change", "success", actorId: userId);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RegenerateCodes(HttpContext ctx, AppDbContext db, RecoveryCodeService recovery, AuditLog audit, CancellationToken ct)
    {
        var userId = ctx.User.UserId();
        var codes = await recovery.RegenerateAsync(db, userId, ct);
        await audit.WriteAsync(db, "auth.recovery_codes.regenerate", "success", actorId: userId);
        return TypedResults.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> ResetMfa(HttpContext ctx, AppDbContext db, UserManager<AppUser> users, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var userId = ctx.User.UserId();
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return TypedResults.Unauthorized();
        user.TotpSecretProtected = null;
        user.TotpPendingSecretProtected = null;
        user.TotpLastTimeStep = null;
        user.TwoFactorEnabled = false;
        user.ProfileVersion++;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user);
        await db.RecoveryCodes.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        await RevokeAllAsync(db, userId, clock, "mfa_reset");
        await ctx.SignOutAsync(AuthConstants.SessionScheme);
        await audit.WriteAsync(db, "auth.mfa.reset", "success", actorId: userId);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UpdateProfile(ProfileRequest req, HttpContext ctx, AppDbContext db, UserManager<AppUser> users, AuditLog audit)
    {
        var v = new Validation();
        var name = v.RequiredText("displayName", req.DisplayName, 60, "El nombre");
        if (req.Version is null) v.Add("version", "Falta la versión.");
        if (!v.IsValid) return v.Problem();
        var user = await users.FindByIdAsync(ctx.User.UserId().ToString());
        if (user is null) return TypedResults.Unauthorized();
        if (user.ProfileVersion != req.Version) return Problems.VersionConflict(new { displayName = user.DisplayName, version = user.ProfileVersion });
        user.DisplayName = name!;
        user.ProfileVersion++;
        var r = await users.UpdateAsync(user);
        if (!r.Succeeded) return Problems.VersionConflict();
        await audit.WriteAsync(db, "profile.update", "success", "user", user.Id);
        return TypedResults.Ok(new { displayName = user.DisplayName, initials = Initials(user), version = user.ProfileVersion });
    }

    private static class DummyHash
    {
        public static readonly string Value = new PasswordHasher<AppUser>(Microsoft.Extensions.Options.Options.Create(
            new PasswordHasherOptions { IterationCount = Program.PasswordIterations })).HashPassword(new AppUser(), "dummy-password-for-timing");
    }
}
