using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Npgsql;
using Xunit;

namespace AlvarezFinanzas.IntegrationTests;

/// <summary>Contratos de seguridad implementados que no tenían prueba propia (cookies, registro, perfil, auditoría, errores, hash).</summary>
public class SecurityContractTests(PostgresFixture pg)
{
    private const string Password = "una frase larga de prueba 123";

    /// <summary>
    /// La cookie efectiva es la última con ese nombre (p. ej. login borra la preautenticación anterior y emite una nueva).
    /// Todas las emitidas, también los borrados, deben cumplir los atributos de __Host-.
    /// </summary>
    private static SetCookieHeaderValue SetCookie(HttpResponseMessage r, string name)
    {
        Assert.True(r.Headers.TryGetValues("Set-Cookie", out var values), $"{r.RequestMessage?.RequestUri} no emitió Set-Cookie");
        var all = SetCookieHeaderValue.ParseList(values.ToList()).Where(c => c.Name == name).ToList();
        Assert.NotEmpty(all);
        foreach (var earlier in all.SkipLast(1)) AssertHostCookie(earlier, earlier.SameSite, deletion: true);
        return all[^1];
    }

    /// <summary>__Host-: Secure, Path=/ y sin Domain (si no, el navegador la rechaza); además HttpOnly y SameSite explícito.</summary>
    private static void AssertHostCookie(SetCookieHeaderValue c, SameSiteMode sameSite, bool deletion = false)
    {
        Assert.StartsWith("__Host-", c.Name.Value);
        Assert.True(c.Secure, $"{c.Name} sin Secure");
        Assert.True(c.HttpOnly, $"{c.Name} sin HttpOnly");
        Assert.Equal("/", c.Path.Value);
        Assert.True(c.Domain.Length == 0, $"{c.Name} con Domain={c.Domain}");
        Assert.Equal(sameSite, c.SameSite);
        if (deletion)
        {
            Assert.True(c.Expires < DateTimeOffset.UtcNow, $"{c.Name} no se borra");
        }
        else
        {
            // Cookie de sesión del navegador: sin remember-me ni vencimiento persistente.
            Assert.Null(c.Expires);
            Assert.Null(c.MaxAge);
        }
    }

    [Fact]
    public async Task Session_preauth_and_csrf_cookies_use_the_host_prefix_and_strict_attributes()
    {
        await using var app = await TestApp.StartAsync(pg);
        await app.CreateUserAsync("augusto", Password);
        var c = app.NewClient();

        var csrf = await c.GetAsync("/api/v1/auth/csrf");
        AssertHostCookie(SetCookie(csrf, "__Host-alvarez-csrf"), SameSiteMode.Strict);
        c.Csrf = (await csrf.Json()).Str("token");

        var login = await c.PostAsync("/api/v1/auth/login", new { username = "augusto", password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        AssertHostCookie(SetCookie(login, "__Host-alvarez-preauth"), SameSiteMode.Lax);

        await c.RefreshCsrfAsync();
        c.TotpSecret = (await (await c.PostAsync("/api/v1/auth/mfa/enroll/start", null)).Json()).Str("secret");
        var confirm = await c.PostAsync("/api/v1/auth/mfa/enroll/confirm", new { code = c.NextCode() });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        AssertHostCookie(SetCookie(confirm, "__Host-alvarez-session"), SameSiteMode.Lax);
        AssertHostCookie(SetCookie(confirm, "__Host-alvarez-preauth"), SameSiteMode.Lax, deletion: true);

        // La rotación por reautenticación emite la referencia nueva con los mismos atributos.
        await c.RefreshCsrfAsync();
        var reauth = await c.PostAsync("/api/v1/auth/reauth", new { password = Password, code = c.NextCode() });
        Assert.Equal(HttpStatusCode.OK, reauth.StatusCode);
        AssertHostCookie(SetCookie(reauth, "__Host-alvarez-session"), SameSiteMode.Lax);

        // Logout la borra con los mismos atributos: un __Host- borrado con otros atributos el navegador lo ignora.
        await c.RefreshCsrfAsync();
        var logout = await c.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertHostCookie(SetCookie(logout, "__Host-alvarez-session"), SameSiteMode.Lax, deletion: true);
    }

    [Fact]
    public async Task There_is_no_public_registration_and_the_cli_refuses_a_second_owner()
    {
        await using var app = await TestApp.StartAsync(pg);
        await app.CreateUserAsync("augusto", Password);
        var c = app.NewClient();
        await c.RefreshCsrfAsync();
        var body = new { username = "intruso", password = "otra frase larga para el intruso", email = "intruso@example.invalid", displayName = "Intruso" };
        foreach (var url in new[] { "/api/v1/auth/register", "/api/v1/auth/signup", "/api/v1/register", "/api/v1/users", "/api/v1/auth/users",
                     "/api/register", "/api/v1/auth/invite", "/api/v1/auth/manage/info" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync(url, body)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(url)).StatusCode);
        }
        // Los endpoints de Identity (MapIdentityApi) tampoco existen fuera de /api.
        foreach (var url in new[] { "/register", "/login", "/manage/info" })
        {
            Assert.False((await c.PostAsync(url, body)).IsSuccessStatusCode, url);
        }
        await using (var db = app.NewDbContext()) Assert.Equal(1, await db.Users.CountAsync());

        // El único alta es la CLI de un solo uso, y se niega si ya existe el propietario (antes de pedir la contraseña).
        var cli = await CliRunner.RunAsync("bootstrap-owner", app.ConnectionString, app.KeyRingPath, environment: "Testing",
            extra: ["--username", "intruso", "--display-name", "Intruso"]);
        Assert.Equal(3, cli.Code);
        Assert.Contains("No hay registro público", cli.Output);
        await using (var db = app.NewDbContext()) Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Profile_edit_requires_current_version_and_recomputes_initials()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var me = (await a.GetJson("/api/v1/auth/me")).GetProperty("user");
        Assert.Equal("augusto Test", me.Str("displayName"));
        Assert.Equal("AT", me.Str("initials"));
        var version = me.GetProperty("version").GetInt64();

        var ok = await a.PatchAsync("/api/v1/auth/me", new { displayName = "  Augusto Álvarez  ", version });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await ok.Json();
        Assert.Equal("Augusto Álvarez", updated.Str("displayName"));
        Assert.Equal("AÁ", updated.Str("initials"));
        Assert.Equal(version + 1, updated.GetProperty("version").GetInt64());
        me = (await a.GetJson("/api/v1/auth/me")).GetProperty("user");
        Assert.Equal("Augusto Álvarez", me.Str("displayName"));
        Assert.Equal("AÁ", me.Str("initials"));

        // Versión vieja: 409 con el estado actual, sin sobrescribir.
        var stale = await a.PatchAsync("/api/v1/auth/me", new { displayName = "Otro Nombre", version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = (await stale.Json()).GetProperty("current");
        Assert.Equal("Augusto Álvarez", current.Str("displayName"));
        Assert.Equal(version + 1, current.GetProperty("version").GetInt64());

        // Una sola palabra: primeras dos letras en mayúscula (es-AR).
        var single = await a.PatchAsync("/api/v1/auth/me", new { displayName = "álvaro", version = version + 1 });
        Assert.Equal("ÁL", (await single.Json()).Str("initials"));

        // Validación del servidor y allowlist estricta.
        var missingVersion = await a.PatchAsync("/api/v1/auth/me", new { displayName = "Sin versión" });
        Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode);
        Assert.True((await missingVersion.Json()).GetProperty("errors").TryGetProperty("version", out _));
        foreach (var bad in new[] { "", "   ", new string('x', 61), "con\u0007control" })
        {
            var r = await a.PatchAsync("/api/v1/auth/me", new { displayName = bad, version = version + 2 });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.True((await r.Json()).GetProperty("errors").TryGetProperty("displayName", out _), bad);
        }
        var massAssignment = await a.PatchAsync("/api/v1/auth/me", new { displayName = "Hack", version = version + 2, userName = "otro", twoFactorEnabled = false });
        Assert.Equal(HttpStatusCode.BadRequest, massAssignment.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.SendAsync(HttpMethod.Patch, "/api/v1/auth/me", new { displayName = "Sin CSRF", version = version + 2 }, csrf: false)).StatusCode);
        var anonymous = app.NewClient();
        await anonymous.RefreshCsrfAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PatchAsync("/api/v1/auth/me", new { displayName = "Anónimo", version = 1 })).StatusCode);

        me = (await a.GetJson("/api/v1/auth/me")).GetProperty("user");
        Assert.Equal("álvaro", me.Str("displayName"));
        Assert.Equal("augusto", me.Str("username"));
        await using var db = app.NewDbContext();
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "profile.update" && e.Result == "success"));
    }

    [Fact]
    public async Task Audit_timestamps_are_utc_instants_even_if_the_database_session_uses_another_time_zone()
    {
        // Todas las conexiones (API incluida) con la zona horaria de sesión de Argentina, no UTC.
        var cs = new NpgsqlConnectionStringBuilder(await pg.CreateDatabaseAsync()) { Timezone = "America/Argentina/Buenos_Aires" }.ConnectionString;
        await TestApp.MigrateAsync(cs);
        await using var app = TestApp.Start(cs);
        var at = new DateTimeOffset(2030, 1, 15, 3, 4, 5, TimeSpan.Zero).AddTicks(1_234_567);
        app.Clock.SetUtcNow(at);
        await app.CreateUserAsync("augusto", Password);
        var c = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.LoginAsync("augusto", "contraseña equivocada larga")).StatusCode);

        await using (var db = app.NewDbContext())
        {
            var failure = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "auth.login" && e.Result == "failure");
            Assert.Equal(TimeSpan.Zero, failure.OccurredAt.Offset);
            Assert.Equal(RecordTestSupport.Micro(at), failure.OccurredAt);
        }
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand("""
            SELECT current_setting('TimeZone'), occurred_at::text, to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US'),
                   (SELECT data_type FROM information_schema.columns WHERE table_name = 'audit_events' AND column_name = 'occurred_at')
            FROM audit_events WHERE action = 'auth.login' AND result = 'failure'
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("America/Argentina/Buenos_Aires", reader.GetString(0));
            Assert.EndsWith("-03", reader.GetString(1)); // la sesión lo muestra en hora local…
            Assert.Equal("2030-01-15T03:04:05.123456", reader.GetString(2)); // …pero el instante guardado es el UTC del servidor
            Assert.Equal("timestamp with time zone", reader.GetString(3));
        }
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task Unhandled_errors_return_problem_details_with_correlation_id_and_no_internals(string environment)
    {
        using var ring = new TestKeyRing();
        var cs = await pg.CreateDatabaseAsync();
        await TestApp.MigrateAsync(cs);
        await using var app = TestApp.Start(cs, ring.Settings(), environment, ring.KeysPath);
        var a = await app.SignedInAsync();
        // Falla real de la base: el mensaje de Npgsql incluye SQLSTATE, nombre de relación y SQL.
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("ALTER TABLE entries RENAME TO entries_fuera_de_servicio", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var r = await a.GetAsync("/api/v1/entries");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var text = await r.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(500, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.Str("title")));
        var correlation = body.Str("correlationId");
        Assert.Matches("^[0-9a-f]{32}$", correlation);
        // El header sobrevive al manejador de excepciones (que limpia los headers antes de escribir el 500).
        Assert.True(r.Headers.TryGetValues("X-Correlation-Id", out var header), "500 sin X-Correlation-Id");
        Assert.Equal(correlation, Assert.Single(header));
        Assert.False(body.TryGetProperty("exception", out _));
        Assert.False(body.TryGetProperty("detail", out _));
        foreach (var leak in new[] { "Npgsql", "Postgres", "42P01", "relation \\\"", "does not exist", "SELECT", "Exception", "   at ", ".cs:line", "Host=", "Password" })
        {
            Assert.DoesNotContain(leak, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static (int Prf, int Iterations, int SaltLength) DecodeV3(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        Assert.Equal(0x01, bytes[0]); // formato v3 de Identity
        return ((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1)), (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9)));
    }

    [Theory]
    [InlineData("v3-100k")]
    [InlineData("v2")]
    public async Task Login_rehashes_passwords_stored_with_an_older_hash_policy(string oldPolicy)
    {
        await using var app = await TestApp.StartAsync(pg);
        await app.CreateUserAsync("augusto", Password);
        var older = oldPolicy == "v2"
            ? new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }
            : new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = 100_000 };
        var oldHash = new PasswordHasher<AppUser>(Options.Create(older)).HashPassword(new AppUser(), Password);
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByNameAsync("augusto"))!;
            // El hash actual (creado por la app) ya cumple la política: 210.000 iteraciones de PBKDF2-HMAC-SHA512.
            Assert.Equal((2, Program.PasswordIterations, 16), DecodeV3(user.PasswordHash!));
            user.PasswordHash = oldHash;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }

        var c = app.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await c.LoginAsync("augusto", Password)).StatusCode);

        string rehashed;
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByNameAsync("augusto"))!;
            rehashed = user.PasswordHash!;
            Assert.NotEqual(oldHash, rehashed);
            Assert.Equal((2, Program.PasswordIterations, 16), DecodeV3(rehashed)); // PRF 2 = HMAC-SHA512
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
            Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, rehashed, Password));
        }
        // Un login posterior no vuelve a re-hashear (la política ya se cumple) y una contraseña incorrecta no lo dispara.
        Assert.Equal(HttpStatusCode.OK, (await app.NewClient().LoginAsync("augusto", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewClient().LoginAsync("augusto", "contraseña equivocada larga")).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var user = (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("augusto"))!;
            Assert.Equal(rehashed, user.PasswordHash);
        }
    }
}
