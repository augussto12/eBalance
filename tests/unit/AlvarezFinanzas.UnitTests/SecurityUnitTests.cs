using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Claims;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlvarezFinanzas.UnitTests;

/// <summary>SEC-05: PBKDF2-HMAC-SHA512 (formato v3 de Identity) con 210.000 iteraciones, coste medido y re-hash al cambiar la política.</summary>
public class PasswordHashingTests(ITestOutputHelper output)
{
    private const string Password = "una frase larga de prueba 123";

    private static PasswordHasher<AppUser> Hasher(PasswordHasherOptions o) => new(Options.Create(o));

    /// <summary>La misma configuración que registra Program.ConfigureServices.</summary>
    private static PasswordHasher<AppUser> AppHasher() => Hasher(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = AlvarezFinanzas.Program.PasswordIterations,
    });

    [Fact]
    public void Hash_format_is_v3_pbkdf2_hmac_sha512_with_210k_iterations_and_128_bit_salt()
    {
        var bytes = Convert.FromBase64String(AppHasher().HashPassword(new AppUser(), Password));
        Assert.Equal(0x01, bytes[0]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1))); // KeyDerivationPrf.HMACSHA512
        Assert.Equal(210_000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9)));
        Assert.Equal(1 + 4 + 4 + 4 + 16 + 32, bytes.Length); // subclave de 256 bits
    }

    [Fact]
    public void Hashes_from_an_older_policy_need_rehash_and_current_ones_do_not()
    {
        var current = AppHasher();
        var fewerIterations = Hasher(new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = 100_000 })
            .HashPassword(new AppUser(), Password);
        var v2 = Hasher(new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }).HashPassword(new AppUser(), Password);
        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, current.VerifyHashedPassword(new AppUser(), fewerIterations, Password));
        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, current.VerifyHashedPassword(new AppUser(), v2, Password));
        Assert.Equal(PasswordVerificationResult.Success, current.VerifyHashedPassword(new AppUser(), current.HashPassword(new AppUser(), Password), Password));
        Assert.Equal(PasswordVerificationResult.Failed, current.VerifyHashedPassword(new AppUser(), fewerIterations, "otra contraseña larga 1"));
    }

    /// <summary>
    /// Mide lo que paga cada login (una verificación) en esta máquina y lo imprime. Solo afirma un piso holgado: si el coste
    /// cae por debajo de 5 ms, la política dejó de aplicarse (p. ej. iteraciones reducidas por error).
    /// </summary>
    [Fact]
    public void Measured_cost_of_one_verification_is_reported()
    {
        var hasher = AppHasher();
        var hash = hasher.HashPassword(new AppUser(), Password);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(new AppUser(), hash, Password)); // calentamiento
        var samples = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = hasher.VerifyHashedPassword(new AppUser(), hash, Password);
            sw.Stop();
            Assert.Equal(PasswordVerificationResult.Success, r);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var median = samples[samples.Count / 2];
        var line = $"PBKDF2-HMAC-SHA512, {AlvarezFinanzas.Program.PasswordIterations:N0} iteraciones: mediana {median:F1} ms por verificación " +
                   $"(mín {samples[0]:F1} ms, máx {samples[^1]:F1} ms, n={samples.Count}) en {Environment.ProcessorCount} CPU {RuntimeInformation.ProcessArchitecture}, " +
                   $"{RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}";
        output.WriteLine(line);
        TestContext.Current.SendDiagnosticMessage(line);
        Assert.True(median > 5, line);
    }
}

/// <summary>02-MOD-17: el propietario de los filtros globales sale solo de una sesión completa (con MFA).</summary>
public class SessionOwnerTests
{
    private static Guid? Owner(HttpContext? ctx) => new SessionOwner(new HttpContextAccessor { HttpContext = ctx }).OwnerId;

    private static DefaultHttpContext Context(string? authenticationType, params Claim[] claims) =>
        new() { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType)) };

    [Fact]
    public void Owner_comes_only_from_a_full_mfa_session()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, Owner(Context(AuthConstants.SessionScheme, new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(AuthConstants.ClaimAmr, "mfa"))));
        // Preautenticación (contraseña sin MFA), anónimo, sin request (CLI/segundo plano) o id inválido: sin propietario.
        Assert.Null(Owner(Context(AuthConstants.PreAuthScheme, new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(AuthConstants.ClaimStage, "mfa"))));
        Assert.Null(Owner(Context(null, new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(AuthConstants.ClaimAmr, "mfa"))));
        Assert.Null(Owner(new DefaultHttpContext()));
        Assert.Null(Owner(null));
        Assert.Null(Owner(Context(AuthConstants.SessionScheme, new Claim(ClaimTypes.NameIdentifier, "no-es-un-guid"), new Claim(AuthConstants.ClaimAmr, "mfa"))));
    }
}
