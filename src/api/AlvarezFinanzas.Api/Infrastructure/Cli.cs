using System.Text;
using System.Text.RegularExpressions;
using AlvarezFinanzas.Api;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlvarezFinanzas.Infrastructure;

/// <summary>
/// Comandos operativos ejecutados en consola confiable (docker compose run). Las contraseñas se leen por stdin,
/// nunca por argumentos, variables de entorno ni archivos de Compose.
/// </summary>
public static partial class Cli
{
    private static readonly string[] Commands = ["migrate", "bootstrap-owner", "recover-owner", "revoke-sessions", "purge-audit", "check-config", "verify-data", "reset-key-ring"];
    public const string ResetKeyRingConfirmation = "ACEPTAR-PERDIDA";
    public static readonly string[] DefaultCategories = ["Otros", "Alimentación", "Hogar", "Servicios", "Tecnología", "Transporte", "Ocio", "Trabajo", "Salud"];

    public static bool IsCommand(string arg) => Commands.Contains(arg);

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0];
        var rest = args[1..];
        try
        {
            return command switch
            {
                "migrate" => await MigrateAsync(rest),
                "purge-audit" => await PurgeAuditAsync(rest),
                _ => await WithAppAsync(rest, command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static IConfiguration LoadConfig(string[] args) =>
        new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables("ALVAREZ_")
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

    private static AppDbContext MigratorContext(IConfiguration config)
    {
        var cs = SecretFiles.BuildConnectionString(config, "Migrator");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options;
        return new AppDbContext(options, TimeProvider.System);
    }

    /// <summary>Aplica migraciones con el rol migrador y otorga al rol runtime solo DML (sin UPDATE/DELETE en auditoría).</summary>
    private static async Task<int> MigrateAsync(string[] args)
    {
        var config = LoadConfig(args);
        var runtimeRole = config["App:RuntimeDbRole"] ?? "alvarez_app";
        if (!RoleName().IsMatch(runtimeRole)) throw new InvalidOperationException("App:RuntimeDbRole no es un nombre de rol válido.");
        await using var db = MigratorContext(config);
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        Console.WriteLine(pending.Count == 0 ? "Sin migraciones pendientes." : $"Aplicando {pending.Count} migración(es): {string.Join(", ", pending)}");
        await db.Database.MigrateAsync();
        if (config["App:SkipGrants"] != "true")
        {
            // Identificador validado con regex: no se puede parametrizar un nombre de rol.
            var sql = $"""
                GRANT USAGE ON SCHEMA public TO {runtimeRole};
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {runtimeRole};
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {runtimeRole};
                REVOKE UPDATE, DELETE, TRUNCATE ON audit_events FROM {runtimeRole};
                REVOKE ALL ON "__EFMigrationsHistory" FROM {runtimeRole};
                GRANT SELECT ON "__EFMigrationsHistory" TO {runtimeRole};
                """;
            await db.Database.ExecuteSqlRawAsync(sql);
            Console.WriteLine($"Permisos del rol {runtimeRole} actualizados (DML; auditoría solo INSERT/SELECT; historial de migraciones solo SELECT).");
        }
        return 0;
    }

    private static async Task<int> PurgeAuditAsync(string[] args)
    {
        var config = LoadConfig(args);
        var days = int.TryParse(config["App:AuditRetentionDays"], out var d) ? d : 90;
        await using var db = MigratorContext(config);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var n = await db.AuditEvents.Where(a => a.OccurredAt < cutoff).ExecuteDeleteAsync();
        Console.WriteLine($"Eventos de auditoría eliminados (más de {days} días): {n}");
        return 0;
    }

    private static async Task<int> WithAppAsync(string[] args, string command)
    {
        var app = Program.Build(args);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        switch (command)
        {
            case "bootstrap-owner":
                return await BootstrapOwnerAsync(args, db, users);
            case "recover-owner":
                return await RecoverOwnerAsync(args, db, users);
            case "revoke-sessions":
            {
                var n = await db.Sessions.Where(s => s.RevokedAt == null).ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow).SetProperty(x => x.RevokedReason, "cli_revoke_all")
                    .SetProperty(x => x.ProtectedTicket, (byte[]?)null));
                db.AuditEvents.Add(new AuditEvent { Action = "cli.revoke_sessions", Result = "success", OccurredAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
                Console.WriteLine($"Sesiones revocadas: {n}");
                return 0;
            }
            case "check-config":
                return await CheckConfigAsync(sp, db);
            case "verify-data":
                return await VerifyDataAsync(sp, db, args.Contains("--revoke-sessions"));
            case "reset-key-ring":
                return await ResetKeyRingAsync(sp, db, users);
            default:
                return 2;
        }
    }

    private static async Task<int> BootstrapOwnerAsync(string[] args, AppDbContext db, UserManager<AppUser> users)
    {
        if (await db.Users.AnyAsync())
        {
            Console.Error.WriteLine("Ya existe un propietario. No hay registro público ni alta de usuarios adicionales.");
            return 3;
        }
        var username = Arg(args, "--username") ?? Prompt("Usuario: ");
        var displayName = Arg(args, "--display-name") ?? Prompt("Nombre para mostrar: ");
        var password = ReadNewPassword();
        var user = new AppUser { UserName = username, DisplayName = displayName.Trim(), CreatedAt = DateTimeOffset.UtcNow, LockoutEnabled = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine("No se pudo crear el propietario: " + string.Join("; ", result.Errors.Select(e => e.Description)));
            return 4;
        }
        foreach (var name in DefaultCategories) db.Categories.Add(new Category { OwnerId = user.Id, Name = name, Kind = CategoryKind.Any });
        db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, Action = "cli.bootstrap_owner", Result = "success", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Console.WriteLine("Propietario creado. Iniciá sesión y configurá el segundo factor antes de cargar datos reales.");
        return 0;
    }

    /// <summary>Recuperación operativa con acceso SSH autorizado: nueva contraseña, MFA reiniciado y todas las sesiones revocadas.</summary>
    private static async Task<int> RecoverOwnerAsync(string[] args, AppDbContext db, UserManager<AppUser> users)
    {
        var username = Arg(args, "--username") ?? Prompt("Usuario a recuperar: ");
        var user = await users.FindByNameAsync(username);
        if (user is null) { Console.Error.WriteLine("Usuario inexistente."); return 3; }
        var confirm = Prompt($"Esto reinicia contraseña y segundo factor de «{user.UserName}» y cierra todas sus sesiones. Escribí el usuario para confirmar: ");
        if (confirm != user.UserName) { Console.Error.WriteLine("Cancelado."); return 5; }
        var password = ReadNewPassword();
        await users.RemovePasswordAsync(user);
        var reset = await users.AddPasswordAsync(user, password);
        if (!reset.Succeeded)
        {
            Console.Error.WriteLine("No se pudo cambiar la contraseña: " + string.Join("; ", reset.Errors.Select(e => e.Description)));
            return 4;
        }
        user.TotpSecretProtected = null;
        user.TotpPendingSecretProtected = null;
        user.TotpLastTimeStep = null;
        user.TwoFactorEnabled = false;
        user.ProfileVersion++;
        await users.UpdateAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await users.UpdateSecurityStampAsync(user);
        await db.RecoveryCodes.Where(r => r.UserId == user.Id).ExecuteDeleteAsync();
        await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow).SetProperty(x => x.RevokedReason, "cli_recovery")
            .SetProperty(x => x.ProtectedTicket, (byte[]?)null));
        db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, Action = "cli.recover_owner", Result = "success", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Console.WriteLine("Cuenta recuperada. Al iniciar sesión se pedirá configurar el segundo factor de nuevo.");
        return 0;
    }

    private static async Task<int> CheckConfigAsync(IServiceProvider sp, AppDbContext db)
    {
        var ok = true;
        var options = sp.GetRequiredService<IOptions<AppOptions>>().Value;
        void Check(bool condition, string label)
        {
            Console.WriteLine($"{(condition ? "OK  " : "FAIL")} {label}");
            ok &= condition;
        }
        Check(options.PublicOrigin.StartsWith("https://", StringComparison.Ordinal), "Origen público HTTPS");
        Check(await db.Database.CanConnectAsync(), "Conexión a PostgreSQL (rol runtime)");
        try
        {
            Check(!(await db.Database.GetPendingMigrationsAsync()).Any(), "Sin migraciones pendientes");
        }
        catch (Npgsql.PostgresException ex)
        {
            Check(false, "Historial de migraciones legible: " + ex.SqlState);
        }
        // Nada de lo que sigue protege datos ni crea claves: un Protect() aquí generaría una clave en un key ring vacío y
        // ocultaría su pérdida. Las claves se cuentan antes de tocar Data Protection y se verifica con el key ring de solo lectura.
        var keyDir = new DirectoryInfo(options.KeyRingPath);
        Check(keyDir.Exists, "Directorio del key ring presente");
        var keyFiles = keyDir.Exists ? keyDir.GetFiles("key-*.xml") : [];
        Check(keyFiles.Length > 0, "Key ring con claves");
        if (keyFiles.Length > 0)
        {
            var plain = keyFiles.Any(f => !File.ReadAllText(f.FullName).Contains("encryptedSecret", StringComparison.Ordinal));
            Check(!plain, "Claves del key ring cifradas con certificado");
        }
        try
        {
            var report = await sp.GetRequiredService<KeyRingGuard>().VerifyAsync(db);
            Check(report.Status == KeyRingStatus.Verified, "Data Protection: " + report.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Check(false, "Data Protection: " + ex.GetType().Name);
        }
        Check(await db.Users.CountAsync() <= 1, "Un único propietario");
        var owner = await db.Users.OrderBy(u => u.CreatedAt).FirstOrDefaultAsync();
        Check(owner is null || owner.TwoFactorEnabled, "Segundo factor configurado (o propietario aún no creado)");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Procesos operativos que recorren TODOS los propietarios (sin sesión): ignoran explícitamente el filtro global por owner.
    /// </summary>
    private static IQueryable<T> AllOwners<T>(DbSet<T> set) where T : class => set.IgnoreQueryFilters([AppDbContext.OwnerFilter]);

    /// <summary>
    /// Verificación de restauración: conteos, sumas por moneda (enteros exactos), integridad de cuotas y que el centinela y
    /// los secretos TOTP se puedan descifrar con el key ring restaurado (de solo lectura: no crea claves). Opcionalmente
    /// revoca todas las sesiones restauradas.
    /// </summary>
    private static async Task<int> VerifyDataAsync(IServiceProvider sp, AppDbContext db, bool revokeSessions)
    {
        var keyRing = await sp.GetRequiredService<KeyRingGuard>().VerifyAsync(db);
        var secrets = keyRing.TotpSecrets;
        var decryptable = keyRing.TotpDecryptable;
        var sums = await AllOwners(db.Entries).Where(e => e.DeletedAt == null)
            .GroupBy(e => new { e.Currency, e.Kind })
            .Select(g => new { currency = g.Key.Currency.ToString(), kind = g.Key.Kind.ToString(), count = g.Count(), totalMinor = g.Sum(x => x.AmountMinor) })
            .ToListAsync();
        var planSums = await AllOwners(db.Plans).Where(p => p.DeletedAt == null)
            .GroupBy(p => p.Currency)
            .Select(g => new { currency = g.Key.ToString(), count = g.Count(), totalMinor = g.Sum(x => x.TotalMinor) })
            .ToListAsync();
        var badPlans = await AllOwners(db.Plans).CountAsync(p => p.TotalMinor != p.Installments.Sum(i => i.AmountMinor) || p.Count != p.Installments.Count());
        var revoked = 0;
        if (revokeSessions)
        {
            revoked = await db.Sessions.Where(s => s.RevokedAt == null).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow).SetProperty(x => x.RevokedReason, "restore_test")
                .SetProperty(x => x.ProtectedTicket, (byte[]?)null));
        }
        var report = new
        {
            counts = new
            {
                users = await db.Users.CountAsync(),
                accounts = await AllOwners(db.Accounts).CountAsync(),
                categories = await AllOwners(db.Categories).CountAsync(),
                entries = await AllOwners(db.Entries).CountAsync(),
                plans = await AllOwners(db.Plans).CountAsync(),
                installments = await AllOwners(db.Installments).CountAsync(),
                payments = await AllOwners(db.Payments).CountAsync(),
                transfers = await AllOwners(db.Transfers).CountAsync(),
                auditEvents = await db.AuditEvents.CountAsync(),
            },
            entrySums = sums,
            planSums,
            plansWithInconsistentInstallments = badPlans,
            totpSecrets = secrets,
            totpDecryptable = decryptable,
            // Verified / Missing (backup anterior al centinela) / Mismatch (el key ring no corresponde a este dump).
            keyRingSentinel = keyRing.Status.ToString(),
            sessionsRevoked = revoked,
        };
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return badPlans == 0 && decryptable == secrets && keyRing.Status != KeyRingStatus.Mismatch ? 0 : 1;
    }

    /// <summary>
    /// Aceptación explícita y auditada de un key ring perdido sin copia (la API se niega a arrancar). Reinicia el segundo
    /// factor de los usuarios cuyo secreto ya no se puede descifrar, revoca todas las sesiones y registra un centinela nuevo
    /// con las claves actuales. No toca datos financieros. Si el key ring se puede restaurar, restaurarlo en su lugar.
    /// Misma regla que el arranque (<see cref="KeyRingGuard.RefusesStart"/>): también cuando todavía no hay centinela (dump
    /// anterior a esta versión) y algún secreto TOTP no se descifra; antes respondía «nada que reiniciar» y la API seguía
    /// sin arrancar.
    /// </summary>
    private static async Task<int> ResetKeyRingAsync(IServiceProvider sp, AppDbContext db, UserManager<AppUser> users)
    {
        var guard = sp.GetRequiredService<KeyRingGuard>();
        var report = KeyRingGuard.Effective(await guard.VerifyAsync(db));
        if (!KeyRingGuard.RefusesStart(report))
        {
            Console.WriteLine($"No hay nada que reiniciar: {report.Detail}.");
            return 0;
        }
        Console.WriteLine($"El key ring no corresponde a esta base: {report.Detail}.");
        var confirm = Prompt("Esto acepta la pérdida del key ring: reinicia el segundo factor de los usuarios cuyo secreto no se puede " +
                             "descifrar, revoca todas las sesiones y registra un centinela nuevo con las claves actuales. Si tenés una copia " +
                             "del key ring, cancelá y restaurala: solo las claves con ./infra/scripts/restore.sh --keyring-only, o base y claves " +
                             $"con ./infra/scripts/restore.sh <instantánea> --with-keyring. Escribí {ResetKeyRingConfirmation} para confirmar: ");
        if (confirm != ResetKeyRingConfirmation) { Console.Error.WriteLine("Cancelado."); return 5; }

        var reset = 0;
        foreach (var user in await db.Users.Where(u => u.TotpSecretProtected != null || u.TotpPendingSecretProtected != null).ToListAsync())
        {
            user.TotpPendingSecretProtected = null;
            user.TotpPendingCreatedAt = null;
            if (user.TotpSecretProtected is not null && !guard.CanDecryptTotp(user.TotpSecretProtected))
            {
                user.TotpSecretProtected = null;
                user.TotpLastTimeStep = null;
                user.TwoFactorEnabled = false;
                await db.RecoveryCodes.Where(r => r.UserId == user.Id).ExecuteDeleteAsync();
                reset++;
            }
            user.ProfileVersion++;
            await users.UpdateAsync(user);
            await users.UpdateSecurityStampAsync(user);
        }
        var revoked = await db.Sessions.Where(s => s.RevokedAt == null).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow).SetProperty(x => x.RevokedReason, "key_ring_reset")
            .SetProperty(x => x.ProtectedTicket, (byte[]?)null));
        await db.KeyRingSentinels.ExecuteDeleteAsync();
        await guard.CreateAsync(db, "reset");
        db.AuditEvents.Add(new AuditEvent { Action = "cli.reset_key_ring", Result = "success", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Console.WriteLine($"Key ring aceptado. Segundos factores reiniciados: {reset}. Sesiones revocadas: {revoked}. " +
                          "Al iniciar sesión se pedirá configurar el segundo factor de nuevo.");
        return 0;
    }

    private static string Prompt(string label)
    {
        Console.Write(label);
        return (Console.ReadLine() ?? "").Trim();
    }

    private static string ReadNewPassword()
    {
        if (Console.IsInputRedirected)
        {
            var piped = Console.ReadLine() ?? "";
            Validate(piped);
            return piped;
        }
        var first = ReadHidden($"Contraseña nueva ({AuthEndpoints.PasswordMinLength} a {AuthEndpoints.PasswordMaxLength} caracteres): ");
        var second = ReadHidden("Repetila: ");
        if (first != second) throw new InvalidOperationException("Las contraseñas no coinciden.");
        Validate(first);
        return first;

        static void Validate(string p)
        {
            if (p.Length < AuthEndpoints.PasswordMinLength || p.Length > AuthEndpoints.PasswordMaxLength)
                throw new InvalidOperationException($"La contraseña debe tener entre {AuthEndpoints.PasswordMinLength} y {AuthEndpoints.PasswordMaxLength} caracteres.");
        }
    }

    private static string ReadHidden(string label)
    {
        Console.Write(label);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}

/// <summary>Solo para generar migraciones con dotnet-ef. No se usa en ejecución.</summary>
public sealed class DesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=design_time").UseSnakeCaseNamingConvention().Options, TimeProvider.System);
}
