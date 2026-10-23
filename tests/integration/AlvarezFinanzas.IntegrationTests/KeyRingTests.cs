using System.Net;
using System.Text;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlvarezFinanzas.IntegrationTests;

/// <summary>
/// VPS-33 / gate de gestión de claves: si se pierde o se reemplaza el key ring, la API no arranca (en producción) y nada
/// —ni el arranque, ni check-config, ni verify-data— genera claves nuevas en silencio. Ambiente Production real, con el
/// key ring cifrado por certificado como en la VPS.
/// </summary>
public class KeyRingTests(PostgresFixture pg)
{
    private const string Password = "una frase larga de prueba 123";

    private static TestApp StartProduction(string cs, TestKeyRing ring, IDictionary<string, string>? extra = null) =>
        TestApp.Start(cs, ring.Settings(extra), "Production", ring.KeysPath);

    private static KeyRingMismatchException AssertRefusesToStart(string cs, TestKeyRing ring, IDictionary<string, string>? settings = null)
    {
        var ex = Assert.ThrowsAny<Exception>(() => TestApp.Start(cs, settings ?? ring.Settings(), "Production", ring.KeysPath));
        return Find(ex) ?? throw new Xunit.Sdk.XunitException("La API no arrancó, pero no por el key ring: " + ex);

        static KeyRingMismatchException? Find(Exception? e) => e switch
        {
            null => null,
            KeyRingMismatchException k => k,
            AggregateException a => a.InnerExceptions.Select(Find).FirstOrDefault(x => x is not null),
            _ => Find(e.InnerException),
        };
    }

    private static AppDbContext Db(string cs) => new(TestApp.DbOptions(cs), TimeProvider.System);

    private static async Task<List<string>> AuditAsync(string cs)
    {
        await using var db = Db(cs);
        return await db.AuditEvents.OrderBy(a => a.Id).Select(a => a.Action + ":" + a.Result).ToListAsync();
    }

    private async Task<string> MigratedDatabaseAsync()
    {
        var cs = await pg.CreateDatabaseAsync();
        await TestApp.MigrateAsync(cs);
        return cs;
    }

    [Fact]
    public async Task Lost_key_ring_stops_the_api_without_generating_keys_until_it_is_restored()
    {
        using var ring = new TestKeyRing();
        var cs = await MigratedDatabaseAsync();
        string totpSecret;
        // Primera ejecución con la base vacía: crea el centinela (y la primera clave) sin intervención.
        await using (var first = StartProduction(cs, ring))
        {
            totpSecret = (await first.SignedInAsync("augusto", Password)).TotpSecret;
        }
        await using (var db = Db(cs)) Assert.NotNull(await db.KeyRingSentinels.SingleOrDefaultAsync());
        Assert.Contains("system.key_ring.sentinel:created", await AuditAsync(cs));
        var backup = ring.Snapshot();
        Assert.NotEmpty(backup);
        Assert.All(backup.Values, k => Assert.Contains("encryptedSecret", Encoding.UTF8.GetString(k)));
        var healthy = await CliRunner.RunAsync("check-config", cs, ring.KeysPath, ring);
        Assert.True(healthy.Code == 0, healthy.Output);

        // Volumen de claves perdido (Docker lo recrea vacío). Producción no permite desactivar la verificación.
        ring.Lose();
        var refused = AssertRefusesToStart(cs, ring, ring.Settings(new Dictionary<string, string> { ["App:VerifyKeyRingAtStartup"] = "false" }));
        Assert.Contains("--with-keyring", refused.Message);
        Assert.Empty(ring.KeyFiles());

        // Los diagnósticos tampoco crean claves (antes, el Protect de check-config creaba una y el chequeo pasaba).
        var check = await CliRunner.RunAsync("check-config", cs, ring.KeysPath, ring);
        Assert.Equal(1, check.Code);
        Assert.Contains("FAIL Key ring con claves", check.Output);
        Assert.Contains("FAIL Data Protection: el centinela", check.Output);
        Assert.Empty(ring.KeyFiles());
        var verify = await CliRunner.RunAsync("verify-data", cs, ring.KeysPath, ring);
        Assert.Equal(1, verify.Code);
        Assert.Contains("\"keyRingSentinel\": \"Mismatch\"", verify.Output);
        Assert.Contains("\"totpDecryptable\": 0", verify.Output);
        Assert.Empty(ring.KeyFiles());

        // Restaurar el key ring del backup: arranca y el segundo factor guardado sigue sirviendo, sin claves nuevas.
        ring.Restore(backup);
        await using var restored = StartProduction(cs, ring);
        restored.Clock.Advance(TimeSpan.FromHours(1));
        var c = restored.NewClient();
        c.TotpSecret = totpSecret;
        Assert.Equal("mfa_required", (await (await c.LoginAsync("augusto", Password)).Json()).Str("state"));
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/v1/auth/mfa/verify", new { code = c.NextCode() })).StatusCode);
        Assert.Equal(backup.Keys.Order(StringComparer.Ordinal), ring.KeyFiles().Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var verified = await CliRunner.RunAsync("verify-data", cs, ring.KeysPath, ring);
        Assert.True(verified.Code == 0, verified.Output);
        Assert.Contains("\"keyRingSentinel\": \"Verified\"", verified.Output);
    }

    [Fact]
    public async Task Key_ring_from_another_installation_or_certificate_without_its_predecessor_refuses_to_start()
    {
        using var ring = new TestKeyRing();
        using var other = new TestKeyRing();
        var cs = await MigratedDatabaseAsync();
        await using (var app = StartProduction(cs, ring)) await app.SignedInAsync("augusto", Password);
        await using (StartProduction(await MigratedDatabaseAsync(), other)) { }
        var otherKeys = other.KeyFiles();
        var ownKeys = ring.KeyFiles();
        Assert.NotEmpty(otherKeys);

        // Volumen reemplazado por el de otra instalación (válido, con su certificado).
        Assert.Contains("no se puede descifrar", AssertRefusesToStart(cs, other).Message);
        Assert.Equal(otherKeys, other.KeyFiles());

        // Mismo volumen, pero el certificado es otro y falta el anterior (previous-*.pfx).
        var wrongCert = new Dictionary<string, string>
        {
            ["App:KeyRingCertificatePath"] = other.CertificatePath,
            ["App:KeyRingCertificatePasswordFile"] = other.PasswordPath,
        };
        AssertRefusesToStart(cs, ring, wrongCert);
        Assert.Equal(ownKeys, ring.KeyFiles());
    }

    [Fact]
    public async Task Database_from_before_the_sentinel_is_adopted_only_if_its_totp_secrets_decrypt()
    {
        using var ring = new TestKeyRing();
        var cs = await MigratedDatabaseAsync();
        await using (var app = StartProduction(cs, ring)) await app.SignedInAsync("augusto", Password);

        // Base de una versión anterior (sin centinela) con su key ring correcto: se adopta.
        await using (var db = Db(cs)) await db.KeyRingSentinels.ExecuteDeleteAsync();
        await using (StartProduction(cs, ring)) { }
        await using (var db = Db(cs)) Assert.NotNull(await db.KeyRingSentinels.SingleOrDefaultAsync());
        Assert.Contains("system.key_ring.sentinel:adopted", await AuditAsync(cs));

        // Sin centinela y con el key ring perdido: no se adopta un key ring que no descifra los secretos existentes.
        await using (var db = Db(cs)) await db.KeyRingSentinels.ExecuteDeleteAsync();
        ring.Lose();
        Assert.Contains("secretos TOTP", AssertRefusesToStart(cs, ring).Message);
        Assert.Empty(ring.KeyFiles());
        await using (var db = Db(cs)) Assert.Null(await db.KeyRingSentinels.SingleOrDefaultAsync());
    }

    [Fact]
    public async Task Totp_secret_protected_with_a_key_missing_from_the_ring_also_stops_the_api()
    {
        using var ring = new TestKeyRing();
        using var foreign = new TestKeyRing();
        var cs = await MigratedDatabaseAsync();
        await using (var app = StartProduction(cs, ring)) await app.SignedInAsync("augusto", Password);
        // Pérdida parcial (p. ej. key ring restaurado de un backup viejo): el centinela se descifra, el secreto TOTP no.
        var protector = DataProtectionProvider.Create(new DirectoryInfo(foreign.KeysPath), b => b.SetApplicationName("alvarez-finanzas-tests"))
            .CreateProtector(TotpService.Purpose);
        var secret = protector.Protect("JBSWY3DPEHPK3PXP");
        await using (var db = Db(cs)) await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.TotpSecretProtected, secret));
        var keys = ring.KeyFiles();
        Assert.Contains("1 de 1 secretos TOTP", AssertRefusesToStart(cs, ring).Message);
        Assert.Equal(keys, ring.KeyFiles());
    }

    [Fact]
    public async Task Reset_key_ring_accepts_the_loss_only_with_explicit_confirmation_and_resets_mfa_and_sessions()
    {
        using var ring = new TestKeyRing();
        var cs = await MigratedDatabaseAsync();
        await using (var app = StartProduction(cs, ring)) await app.SignedInAsync("augusto", Password);
        ring.Lose();
        AssertRefusesToStart(cs, ring);

        var cancelled = await CliRunner.RunAsync("reset-key-ring", cs, ring.KeysPath, ring, input: "si\n");
        Assert.Equal(5, cancelled.Code);
        Assert.Empty(ring.KeyFiles());
        AssertRefusesToStart(cs, ring);

        var reset = await CliRunner.RunAsync("reset-key-ring", cs, ring.KeysPath, ring, input: Cli.ResetKeyRingConfirmation + "\n");
        Assert.True(reset.Code == 0, reset.Output);
        Assert.Contains("Segundos factores reiniciados: 1", reset.Output);
        // La clave nueva existe por una decisión explícita y auditada del operador, no en silencio.
        Assert.NotEmpty(ring.KeyFiles());
        var audit = await AuditAsync(cs);
        Assert.Contains("system.key_ring.sentinel:reset", audit);
        Assert.Contains("cli.reset_key_ring:success", audit);
        await using (var db = Db(cs))
        {
            var user = await db.Users.SingleAsync();
            Assert.False(user.TwoFactorEnabled);
            Assert.Null(user.TotpSecretProtected);
            Assert.Equal(0, await db.RecoveryCodes.CountAsync());
            Assert.Equal(0, await db.Sessions.CountAsync(s => s.RevokedAt == null));
        }

        await using var restarted = StartProduction(cs, ring);
        var c = restarted.NewClient();
        Assert.Equal("enroll_required", (await (await c.LoginAsync("augusto", Password)).Json()).Str("state"));
        var again = await CliRunner.RunAsync("reset-key-ring", cs, ring.KeysPath, ring);
        Assert.Equal(0, again.Code);
        Assert.Contains("No hay nada que reiniciar", again.Output);
    }
}
