using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AlvarezFinanzas.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtpNet;

namespace AlvarezFinanzas.Infrastructure.Auth;

public static class IpMinimizer
{
    /// <summary>Reduce la IP a /24 (IPv4) o /48 (IPv6) para auditoría y metadatos de sesión.</summary>
    public static string? Prefix(IPAddress? ip)
    {
        if (ip is null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4) return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        for (var i = 6; i < bytes.Length; i++) bytes[i] = 0;
        return new IPAddress(bytes) + "/48";
    }
}

/// <summary>TOTP (RFC 6238) con Otp.NET. Ventana ±1 paso de 30 s y bloqueo atómico de reutilización por usuario.</summary>
public sealed class TotpService(IDataProtectionProvider dp, TimeProvider clock)
{
    private readonly IDataProtector _protector = dp.CreateProtector("AlvarezFinanzas.Totp.v1");
    public const string Issuer = "Álvarez Finanzas";

    public string NewProtectedSecret(out string base32)
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        base32 = Base32Encoding.ToString(secret);
        return _protector.Protect(base32);
    }

    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);

    public static string OtpAuthUri(string accountName, string base32) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(accountName)}?secret={base32}&issuer={Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits=6&period=30";

    /// <summary>Devuelve el timestep coincidente o null. No aplica la regla anti-reutilización (ver <see cref="TryConsumeAsync"/>).</summary>
    public long? Match(string protectedSecret, string code)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return null;
        var secret = Base32Encoding.ToBytes(Unprotect(protectedSecret));
        var totp = new Totp(secret);
        var ok = totp.VerifyTotp(clock.GetUtcNow().UtcDateTime, code, out var step, new VerificationWindow(previous: 1, future: 1));
        return ok ? step : null;
    }

    /// <summary>
    /// Acepta el código solo si su timestep es posterior al último usado por el usuario. UPDATE condicional atómico.
    /// </summary>
    public async Task<bool> TryConsumeAsync(AppDbContext db, Guid userId, string protectedSecret, string code, CancellationToken ct)
    {
        var step = Match(protectedSecret, code);
        if (step is null) return false;
        var updated = await db.Users
            .Where(u => u.Id == userId && (u.TotpLastTimeStep == null || u.TotpLastTimeStep < step))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TotpLastTimeStep, step), ct);
        return updated == 1;
    }
}

/// <summary>Códigos de recuperación: 80 bits, mostrados una vez, guardados como SHA-256, consumo atómico.</summary>
public sealed class RecoveryCodeService(TimeProvider clock)
{
    public const int CodeCount = 10;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Normalize(string code) =>
        new(code.Trim().ToUpperInvariant().Where(c => c != '-' && c != ' ').ToArray());

    public static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes("alvarez-recovery:" + Normalize(code)));

    public async Task<IReadOnlyList<string>> RegenerateAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        await db.RecoveryCodes.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        var codes = new List<string>();
        var now = clock.GetUtcNow();
        for (var i = 0; i < CodeCount; i++)
        {
            var chars = new char[16];
            for (var j = 0; j < chars.Length; j++) chars[j] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            var code = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}-{new string(chars, 12, 4)}";
            codes.Add(code);
            db.RecoveryCodes.Add(new RecoveryCode { UserId = userId, CodeHash = Hash(code), CreatedAt = now });
        }
        await db.SaveChangesAsync(ct);
        return codes;
    }

    public async Task<bool> TryConsumeAsync(AppDbContext db, Guid userId, string code, CancellationToken ct)
    {
        var normalized = Normalize(code);
        if (normalized.Length != 16) return false;
        var hash = Hash(normalized);
        var now = clock.GetUtcNow();
        var updated = await db.RecoveryCodes
            .Where(r => r.UserId == userId && r.CodeHash == hash && r.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAt, now), ct);
        return updated == 1;
    }

    public Task<int> RemainingAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        db.RecoveryCodes.CountAsync(r => r.UserId == userId && r.UsedAt == null, ct);
}

/// <summary>
/// Límites de intentos por IP, por cuenta normalizada (HMAC, nunca el nombre en claro) y por usuario.
/// En memoria: un único proceso de API. Si se escalara horizontalmente, mover a almacenamiento compartido.
/// </summary>
public sealed class AttemptLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _loginIp;
    private readonly PartitionedRateLimiter<string> _loginAccount;
    private readonly PartitionedRateLimiter<string> _mfa;
    private readonly PartitionedRateLimiter<string> _bulk;
    private readonly byte[] _accountKey = RandomNumberGenerator.GetBytes(32);

    public AttemptLimiter(IOptions<AppOptions> options)
    {
        var o = options.Value;
        _loginIp = Sliding(o.LoginPerIpPerMinute, TimeSpan.FromMinutes(1));
        _loginAccount = Sliding(o.LoginPerAccountPerMinute, TimeSpan.FromMinutes(1));
        _mfa = Sliding(o.MfaPerMinute, TimeSpan.FromMinutes(1));
        _bulk = Sliding(o.ImportExportPerHour, TimeSpan.FromHours(1));
    }

    private static PartitionedRateLimiter<string> Sliding(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetSlidingWindowLimiter(key, _ =>
            new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    public string AccountKey(string username) =>
        Convert.ToHexString(HMACSHA256.HashData(_accountKey, Encoding.UTF8.GetBytes(username.Trim().ToUpperInvariant())));

    public LimitResult LoginByIp(string ip) => Acquire(_loginIp, "ip:" + ip, TimeSpan.FromMinutes(1));
    public LimitResult LoginByAccount(string accountKey) => Acquire(_loginAccount, "acct:" + accountKey, TimeSpan.FromMinutes(1));
    public LimitResult Mfa(string key) => Acquire(_mfa, "mfa:" + key, TimeSpan.FromMinutes(1));
    public LimitResult Bulk(Guid userId) => Acquire(_bulk, "bulk:" + userId, TimeSpan.FromHours(1));

    private static LimitResult Acquire(PartitionedRateLimiter<string> limiter, string key, TimeSpan fallback)
    {
        using var lease = limiter.AttemptAcquire(key);
        if (lease.IsAcquired) return LimitResult.Allowed;
        var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out var r) ? r : fallback;
        return new LimitResult(false, retry);
    }

    public void Dispose()
    {
        _loginIp.Dispose();
        _loginAccount.Dispose();
        _mfa.Dispose();
        _bulk.Dispose();
    }
}

public readonly record struct LimitResult(bool IsAllowed, TimeSpan RetryAfter)
{
    public static readonly LimitResult Allowed = new(true, TimeSpan.Zero);
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new UnauthorizedAccessException();

    public static Guid? SessionId(this ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(AuthConstants.ClaimSessionId), out var id) ? id : null;
}
