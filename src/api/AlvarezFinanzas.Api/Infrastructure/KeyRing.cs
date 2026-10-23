using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlvarezFinanzas.Infrastructure;

public static class KeyRingSetup
{
    /// <summary>
    /// Key ring persistido y cifrado explícitamente con certificado. Falla cerrado en producción si falta la ruta o el
    /// certificado. Lo usan la API y el verificador de solo lectura (<see cref="ReadOnlyKeyRing"/>), con la misma configuración.
    /// </summary>
    public static IDataProtectionBuilder ConfigureKeyRing(this IDataProtectionBuilder dp, AppOptions options, bool production)
    {
        if (string.IsNullOrWhiteSpace(options.DataProtectionApplicationName))
            throw new InvalidOperationException("Falta la configuración App:DataProtectionApplicationName.");
        dp.SetApplicationName(options.DataProtectionApplicationName);
        if (!string.IsNullOrEmpty(options.KeyRingPath))
        {
            dp.PersistKeysToFileSystem(new DirectoryInfo(options.KeyRingPath));
        }
        else if (production)
        {
            throw new InvalidOperationException("Falta App:KeyRingPath en producción.");
        }
        if (!string.IsNullOrEmpty(options.KeyRingCertificatePath))
        {
            var password = string.IsNullOrEmpty(options.KeyRingCertificatePasswordFile)
                ? null
                : SecretFiles.Read(options.KeyRingCertificatePasswordFile, "contraseña del certificado del key ring");
            var cert = X509CertificateLoader.LoadPkcs12FromFile(options.KeyRingCertificatePath, password, X509KeyStorageFlags.EphemeralKeySet);
            // Certificados anteriores (rotación): siguen pudiendo descifrar claves viejas, pero no cifran claves nuevas.
            var all = new List<X509Certificate2> { cert };
            var dir = Path.GetDirectoryName(options.KeyRingCertificatePath);
            if (dir is not null && Directory.Exists(dir))
            {
                foreach (var previous in Directory.GetFiles(dir, "previous-*.pfx").Order(StringComparer.Ordinal))
                {
                    var pwFile = Path.ChangeExtension(previous, ".password");
                    var pw = File.Exists(pwFile) ? SecretFiles.Read(pwFile, "contraseña de certificado anterior") : null;
                    all.Add(X509CertificateLoader.LoadPkcs12FromFile(previous, pw, X509KeyStorageFlags.EphemeralKeySet));
                }
            }
            dp.ProtectKeysWithCertificate(cert).UnprotectKeysWithAnyCertificate([.. all]);
        }
        else if (production)
        {
            throw new InvalidOperationException("Falta App:KeyRingCertificatePath en producción: el key ring debe cifrarse.");
        }
        return dp;
    }
}

/// <summary>
/// Mismo key ring que la API pero sin generación automática de claves: sirve para verificar sin escribir nada. Si el key
/// ring está vacío o no se puede descifrar, desproteger falla en vez de crear una clave nueva (la API, en cambio, la
/// crearía en silencio al primer uso).
/// </summary>
public sealed class ReadOnlyKeyRing : IDisposable
{
    private readonly Lazy<ServiceProvider> _services;

    public ReadOnlyKeyRing(IOptions<AppOptions> options, IHostEnvironment env)
    {
        _services = new Lazy<ServiceProvider>(() =>
        {
            if (string.IsNullOrEmpty(options.Value.KeyRingPath))
                throw new InvalidOperationException("La verificación del key ring requiere App:KeyRingPath.");
            var services = new ServiceCollection();
            services.AddDataProtection().ConfigureKeyRing(options.Value, env.IsProduction()).DisableAutomaticKeyGeneration();
            return services.BuildServiceProvider();
        });
    }

    public IDataProtectionProvider Provider => _services.Value.GetRequiredService<IDataProtectionProvider>();

    public void Dispose()
    {
        if (_services.IsValueCreated) _services.Value.Dispose();
    }
}

public enum KeyRingStatus
{
    /// <summary>El centinela y todos los secretos TOTP se descifran con el key ring actual.</summary>
    Verified,
    /// <summary>Primera ejecución con esta base: no había datos protegidos y se creó el centinela.</summary>
    Created,
    /// <summary>Base anterior al centinela: los secretos TOTP existentes se descifran y se creó el centinela.</summary>
    Adopted,
    /// <summary>Todavía no hay centinela (la API no arrancó nunca con esta base).</summary>
    Missing,
    /// <summary>El key ring no descifra el centinela o algún secreto TOTP: se perdió, se reemplazó o falta un certificado.</summary>
    Mismatch,
}

public sealed record KeyRingReport(KeyRingStatus Status, string Detail, int TotpSecrets, int TotpDecryptable)
{
    public bool Ok => Status is KeyRingStatus.Verified or KeyRingStatus.Created or KeyRingStatus.Adopted;
}

/// <summary>La API no arranca: el key ring no corresponde a la base.</summary>
public sealed class KeyRingMismatchException(string detail) : InvalidOperationException(
    $"Data Protection: el key ring no corresponde a esta base ({detail}). La API no arranca para no generar claves nuevas " +
    "en silencio: los segundos factores y las sesiones guardadas no se podrían descifrar. " + KeyRingGuard.RecoveryHint);

/// <summary>
/// Falla cerrado si se pierde o reemplaza el key ring (docs/04: «no generar silenciosamente claves nuevas al perder volumen»).
/// La primera vez que la API arranca con una base guarda en ella un valor conocido protegido con Data Protection (el
/// centinela). En cada arranque lo desprotege, junto con los secretos TOTP, con el key ring de solo lectura y ANTES de que
/// Data Protection cargue (y eventualmente cree) claves: si falla, la API no arranca y no se escribe ninguna clave.
/// Activo siempre en producción; en otros entornos con App:VerifyKeyRingAtStartup.
/// </summary>
public sealed class KeyRingGuard(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    ReadOnlyKeyRing readOnly,
    IOptions<AppOptions> options,
    IHostEnvironment env,
    TimeProvider clock,
    ILogger<KeyRingGuard> logger) : IHostedLifecycleService
{
    public const string Purpose = "AlvarezFinanzas.KeyRingSentinel.v1";
    public const string ExpectedValue = "alvarez-finanzas:key-ring-sentinel:v1";

    /// <summary>Qué hacer si la API no arranca por el key ring (lo muestran el error de arranque y reset-key-ring).</summary>
    public const string RecoveryHint =
        "Si solo se perdió o se reemplazó el volumen de claves, restauralo sin tocar la base con " +
        "./infra/scripts/restore.sh --keyring-only. Si se perdieron la base y las claves, restaurá ambas con " +
        "./infra/scripts/restore.sh <instantánea> --with-keyring. Si no hay copia del key ring, aceptá la pérdida con " +
        "reset-key-ring.sh (reinicia el segundo factor y revoca las sesiones).";

    /// <summary>
    /// Regla única de «la API no arranca», para el arranque y para reset-key-ring: el centinela o algún secreto TOTP no se
    /// descifra, o todavía no hay centinela y algún secreto TOTP existente no se descifra (no se adopta un key ring que no
    /// corresponde a la base). En ese último caso el estado efectivo es Mismatch.
    /// </summary>
    public static KeyRingReport Effective(KeyRingReport report) =>
        report.Status == KeyRingStatus.Missing && report.TotpDecryptable < report.TotpSecrets
            ? report with
            {
                Status = KeyRingStatus.Mismatch,
                Detail = $"no hay centinela y {report.TotpSecrets - report.TotpDecryptable} de {report.TotpSecrets} secretos TOTP no se pueden descifrar",
            }
            : report;

    public static bool RefusesStart(KeyRingReport report) => Effective(report).Status == KeyRingStatus.Mismatch;

    /// <summary>En producción no se puede desactivar.</summary>
    public bool Enabled => env.IsProduction() || options.Value.VerifyKeyRingAtStartup;

    /// <summary>
    /// StartingAsync corre antes que el StartAsync de cualquier servicio, incluido el de Data Protection, que precarga el
    /// key ring y crearía una clave nueva si lo encuentra vacío.
    /// </summary>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var report = await EnsureAsync(db, cancellationToken);
        if (!report.Ok)
        {
            logger.LogCritical("Key ring rechazado: {Detail}", report.Detail);
            throw new KeyRingMismatchException(report.Detail);
        }
        logger.LogInformation("Key ring verificado ({Status}).", report.Status);
        await RefreshAsync(db, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Verifica sin escribir nada: ni claves (key ring de solo lectura) ni filas. Lo usan también check-config y verify-data.</summary>
    public async Task<KeyRingReport> VerifyAsync(AppDbContext db, CancellationToken ct = default)
    {
        var secrets = await db.Users.AsNoTracking().Where(u => u.TotpSecretProtected != null).Select(u => u.TotpSecretProtected!).ToListAsync(ct);
        var decryptable = secrets.Count(CanDecryptTotp);
        var sentinel = await db.KeyRingSentinels.AsNoTracking().SingleOrDefaultAsync(ct);
        if (sentinel is null)
        {
            return new(KeyRingStatus.Missing, "todavía no hay centinela del key ring en la base", secrets.Count, decryptable);
        }
        var error = TryUnprotect(readOnly.Provider.CreateProtector(Purpose), sentinel.ProtectedValue, out var value);
        if (error is not null)
        {
            return new(KeyRingStatus.Mismatch, $"el centinela creado el {sentinel.CreatedAt:yyyy-MM-dd} no se puede descifrar: {error}", secrets.Count, decryptable);
        }
        if (value != ExpectedValue)
        {
            return new(KeyRingStatus.Mismatch, "el centinela descifrado no tiene el valor esperado", secrets.Count, decryptable);
        }
        if (decryptable < secrets.Count)
        {
            return new(KeyRingStatus.Mismatch, $"{secrets.Count - decryptable} de {secrets.Count} secretos TOTP no se pueden descifrar", secrets.Count, decryptable);
        }
        return new(KeyRingStatus.Verified, "el centinela y los secretos TOTP se descifran con el key ring actual", secrets.Count, decryptable);
    }

    /// <summary>
    /// Arranque: verifica; si todavía no hay centinela lo crea, pero solo si ningún dato protegido existente quedaría
    /// sin poder descifrarse (primera ejecución, o base anterior al centinela cuyo key ring sigue siendo el correcto).
    /// </summary>
    public async Task<KeyRingReport> EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        var report = Effective(await VerifyAsync(db, ct));
        if (report.Status != KeyRingStatus.Missing) return report;
        var adopted = report.TotpSecrets > 0;
        try
        {
            await CreateAsync(db, adopted ? "adopted" : "created", ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Otro proceso lo creó al mismo tiempo: verificar el suyo.
            db.ChangeTracker.Clear();
            return await VerifyAsync(db, ct);
        }
        logger.LogWarning("Centinela del key ring creado ({Kind}). Desde ahora, si el key ring se pierde, la API no arranca.", adopted ? "base existente" : "primera ejecución");
        return report with
        {
            Status = adopted ? KeyRingStatus.Adopted : KeyRingStatus.Created,
            Detail = adopted ? "centinela creado para una base existente cuyos secretos se descifran" : "centinela creado en la primera ejecución",
        };
    }

    /// <summary>
    /// Protege el centinela con el Data Protection de la API (crea la primera clave si el key ring está vacío) y lo guarda.
    /// Solo en la primera ejecución o tras aceptar explícitamente la pérdida del key ring (reset-key-ring).
    /// </summary>
    public async Task CreateAsync(AppDbContext db, string result, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        db.KeyRingSentinels.Add(new KeyRingSentinel
        {
            ProtectedValue = dataProtection.CreateProtector(Purpose).Protect(ExpectedValue),
            CreatedAt = now,
        });
        db.AuditEvents.Add(new AuditEvent { Action = "system.key_ring.sentinel", Result = result, OccurredAt = now });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Después de un arranque verificado: vuelve a proteger con la clave por defecto actual el centinela y los secretos
    /// TOTP que siguen protegidos con una clave anterior. Data Protection nunca vuelve a cifrar datos viejos: sin esto, el
    /// centinela de la primera ejecución (y el TOTP enrolado entonces) dependerían para siempre de la primera clave y de su
    /// certificado, y borrar un previous-*.pfx (o restaurar en otra VPS solo con el certificado actual) impediría arrancar.
    /// Cuando una clave nueva (cifrada con el certificado nuevo) pasa a ser la de por defecto, el arranque siguiente los
    /// actualiza. Las claves viejas siguen en el key ring (no se revocan). Seguro: cada valor nuevo se comprueba con el key
    /// ring de solo lectura leído del disco (la clave está persistida y cifrada con un certificado configurado) antes de
    /// guardarlo, y la fila solo cambia si nadie la cambió antes. Si algo falla, no se toca nada y la API arranca igual.
    /// </summary>
    public async Task<int> RefreshAsync(AppDbContext db, CancellationToken ct = default)
    {
        try
        {
            var refreshed = 0;
            var sentinelProtector = dataProtection.CreateProtector(Purpose);
            var fresh = sentinelProtector.Protect(ExpectedValue);
            var current = KeyIdOf(fresh);
            if (current is null) return 0;
            var sentinel = await db.KeyRingSentinels.AsNoTracking().SingleOrDefaultAsync(ct);
            if (sentinel is not null && !SameKey(KeyIdOf(sentinel.ProtectedValue), current)
                && TryUnprotect(readOnly.Provider.CreateProtector(Purpose), fresh, out var check) is null && check == ExpectedValue)
            {
                refreshed += await db.KeyRingSentinels.Where(x => x.Id == sentinel.Id && x.ProtectedValue == sentinel.ProtectedValue)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedValue, fresh), ct);
            }

            var totpProtector = dataProtection.CreateProtector(TotpService.Purpose);
            var totpReader = readOnly.Provider.CreateProtector(TotpService.Purpose);
            var users = await db.Users.AsNoTracking().Where(u => u.TotpSecretProtected != null)
                .Select(u => new { u.Id, Secret = u.TotpSecretProtected! }).ToListAsync(ct);
            foreach (var user in users.Where(u => !SameKey(KeyIdOf(u.Secret), current)))
            {
                if (TryUnprotect(totpReader, user.Secret, out var plain) is not null || plain is null) continue;
                var again = totpProtector.Protect(plain);
                if (TryUnprotect(totpReader, again, out var roundTrip) is not null || roundTrip != plain) continue;
                refreshed += await db.Users.Where(u => u.Id == user.Id && u.TotpSecretProtected == user.Secret)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.TotpSecretProtected, again), ct);
            }
            if (refreshed > 0)
            {
                db.AuditEvents.Add(new AuditEvent { Action = "system.key_ring.reprotect", Result = "success", OccurredAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Datos protegidos con una clave anterior vueltos a proteger con la clave actual: {Count}.", refreshed);
            }
            return refreshed;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or IOException or DbUpdateException or Npgsql.NpgsqlException)
        {
            logger.LogWarning(ex, "No se pudieron volver a proteger el centinela y los secretos TOTP con la clave actual; se reintenta en el próximo arranque.");
            return 0;
        }
    }

    /// <summary>
    /// Identificador de la clave con la que se protegió un valor: el payload de Data Protection es Base64url de
    /// [encabezado mágico de 4 bytes][id de la clave, 16 bytes][…]. Null si el valor no tiene ese formato.
    /// </summary>
    internal static byte[]? KeyIdOf(string payload)
    {
        byte[] bytes;
        try
        {
            bytes = System.Buffers.Text.Base64Url.DecodeFromChars(payload);
        }
        catch (FormatException)
        {
            return null;
        }
        return bytes.Length >= 20 && bytes.AsSpan(0, 4).SequenceEqual(PayloadMagicHeader) ? bytes[4..20] : null;
    }

    private static ReadOnlySpan<byte> PayloadMagicHeader => [0x09, 0xF0, 0xC9, 0xF0];

    private static bool SameKey(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>Descifra un secreto TOTP con el key ring de solo lectura (no crea claves).</summary>
    public bool CanDecryptTotp(string protectedSecret) =>
        TryUnprotect(readOnly.Provider.CreateProtector(TotpService.Purpose), protectedSecret, out _) is null;

    private static string? TryUnprotect(IDataProtector protector, string payload, out string? value)
    {
        value = null;
        try
        {
            value = protector.Unprotect(payload);
            return null;
        }
        // Clave ausente o revocada, payload inválido (CryptographicException) o key ring sin ninguna clave utilizable con
        // la generación automática desactivada (InvalidOperationException).
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or FormatException)
        {
            return ex.Message;
        }
    }
}
