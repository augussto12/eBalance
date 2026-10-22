using System.Net;
using Xunit;

namespace AlvarezFinanzas.IntegrationTests;

public class AuthSecurityTests(PostgresFixture pg)
{
    [Fact]
    public async Task Private_endpoints_without_session_return_401_json_not_html()
    {
        await using var app = await TestApp.StartAsync(pg);
        var c = app.NewClient();
        foreach (var url in new[] { "/api/v1/entries", "/api/v1/dashboard?month=2026-10&currency=ARS", "/api/v1/accounts", "/api/v1/auth/sessions" })
        {
            var r = await c.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.DoesNotContain("text/html", r.Content.Headers.ContentType?.MediaType ?? "");
            Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/no-existe")).StatusCode);
    }

    [Fact]
    public async Task Login_errors_are_generic_and_password_alone_gives_no_financial_access()
    {
        await using var app = await TestApp.StartAsync(pg);
        await app.CreateUserAsync("augusto", "una frase larga de prueba 123");
        var c = app.NewClient();
        var wrong = await c.LoginAsync("augusto", "contraseña equivocada larga");
        var missing = await c.LoginAsync("nadie", "contraseña equivocada larga");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal((await wrong.Json()).Str("title"), (await missing.Json()).Str("title"));

        var ok = await c.LoginAsync("augusto", "una frase larga de prueba 123");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("enroll_required", (await ok.Json()).Str("state"));
        // Contraseña correcta sin MFA: sin datos financieros ni exportación.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/entries")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/v1/exports", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/v1/entries", new { kind = "income" })).StatusCode);
    }

    [Fact]
    public async Task Preauth_expires_after_five_minutes()
    {
        await using var app = await TestApp.StartAsync(pg);
        await app.CreateUserAsync("augusto", "una frase larga de prueba 123");
        var c = app.NewClient();
        await c.LoginAsync("augusto", "una frase larga de prueba 123");
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("anonymous", (await c.GetJson("/api/v1/auth/me")).Str("state"));
        var r = await c.PostAsync("/api/v1/auth/mfa/enroll/start", null);
        Assert.False(r.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Totp_code_cannot_be_reused_and_recovery_codes_are_single_use()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        Assert.Equal(10, a.RecoveryCodes.Count);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/v1/entries")).StatusCode);

        // Nuevo login con el MISMO código dos veces.
        var b = app.NewClient();
        b.TotpSecret = a.TotpSecret;
        await b.LoginAsync("augusto", a.Password);
        var code = b.NextCode();
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsync("/api/v1/auth/mfa/verify", new { code })).StatusCode);

        var c = app.NewClient();
        await c.LoginAsync("augusto", a.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/v1/auth/mfa/verify", new { code })).StatusCode);

        // Código de recuperación: sirve una vez y revoca las demás sesiones.
        var recovery = a.RecoveryCodes[0];
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/v1/auth/mfa/verify", new { recoveryCode = recovery })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/v1/entries")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/v1/entries")).StatusCode);

        var d = app.NewClient();
        await d.LoginAsync("augusto", a.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await d.PostAsync("/api/v1/auth/mfa/verify", new { recoveryCode = recovery })).StatusCode);
    }

    [Fact]
    public async Task Csrf_missing_foreign_token_or_foreign_origin_is_rejected()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var body = new { name = "Banco", kind = "bank", currency = "ARS" };

        var noToken = await a.SendAsync(HttpMethod.Post, "/api/v1/accounts", body, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);

        var other = app.NewClient();
        await other.RefreshCsrfAsync();
        var saved = a.Csrf;
        a.Csrf = other.Csrf;
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsync("/api/v1/accounts", body)).StatusCode);
        a.Csrf = saved;

        a.OriginHeader = "https://evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsync("/api/v1/accounts", body)).StatusCode);
        a.OriginHeader = TestApp.Origin;

        Assert.Equal(HttpStatusCode.Created, (await a.PostAsync("/api/v1/accounts", body)).StatusCode);
    }

    [Fact]
    public async Task Logout_and_revocation_take_effect_on_next_request()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        // Segunda sesión del mismo usuario.
        var b = app.NewClient();
        b.TotpSecret = a.TotpSecret;
        await b.LoginAsync("augusto", a.Password);
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsync("/api/v1/auth/mfa/verify", new { code = b.NextCode() })).StatusCode);
        await b.RefreshCsrfAsync();

        var sessions = await a.GetJson("/api/v1/auth/sessions");
        Assert.Equal(2, sessions.GetArrayLength());
        var otherId = sessions.EnumerateArray().Single(s => !s.GetProperty("current").GetBoolean()).Str("id");
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/v1/auth/sessions/{otherId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/v1/entries")).StatusCode);

        // Copiar la cookie antes del logout: no debe servir después (revocación en servidor, no solo borrar cookie).
        var replay = app.CreateClient(new() { BaseAddress = new Uri(TestApp.Origin), HandleCookies = false });
        var sessionCookie = a.Cookies.GetCookies(new Uri(TestApp.Origin))["__Host-alvarez-session"]?.Value;
        Assert.NotNull(sessionCookie);
        replay.DefaultRequestHeaders.Add("Cookie", $"__Host-alvarez-session={sessionCookie}");
        Assert.Equal(HttpStatusCode.OK, (await replay.GetAsync("/api/v1/entries")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/v1/entries")).StatusCode);
    }

    [Fact]
    public async Task Idle_timeout_and_absolute_maximum_are_enforced()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        app.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/v1/entries")).StatusCode);

        var b = app.NewClient();
        b.TotpSecret = a.TotpSecret;
        await b.LoginAsync("augusto", a.Password);
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsync("/api/v1/auth/mfa/verify", new { code = b.NextCode() })).StatusCode);
        // Actividad cada 20 minutos: la renovación deslizante no extiende las 12 h.
        // 35 × 20 min = 11 h 40 min (válida); 36 × 20 min = 12 h (vencida).
        for (var i = 0; i < 36; i++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(20));
            var r = await b.GetAsync("/api/v1/entries");
            if (i < 35) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            else Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
    }

    [Fact]
    public async Task Account_rate_limit_cannot_be_bypassed_by_changing_forwarded_ip()
    {
        await using var app = await TestApp.StartAsync(pg);
        await app.CreateUserAsync("augusto", "una frase larga de prueba 123");
        var c = app.NewClient();
        await c.RefreshCsrfAsync();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
        {
            var r = await c.PostAsync("/api/v1/auth/login", new { username = "augusto", password = "incorrecta pero larga 123" },
                new Dictionary<string, string> { ["X-Forwarded-For"] = $"203.0.113.{i}" });
            statuses.Add(r.StatusCode);
            if (r.StatusCode == HttpStatusCode.TooManyRequests) Assert.NotNull(r.Headers.RetryAfter);
        }
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Lockout_after_ten_failures_blocks_even_correct_password()
    {
        // Se elevan los límites por minuto para aislar el lockout de Identity del rate limiting.
        await using var app = await TestApp.StartAsync(pg, new Dictionary<string, string> { ["App:LoginPerAccountPerMinute"] = "100", ["App:LoginPerIpPerMinute"] = "100" });
        await app.CreateUserAsync("augusto", "una frase larga de prueba 123");
        var c = app.NewClient();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.LoginAsync("augusto", "incorrecta pero larga 123")).StatusCode);
        }
        var r = await c.LoginAsync("augusto", "una frase larga de prueba 123");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Export_requires_recent_reauthentication_and_is_not_cacheable()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsync("/api/v1/exports", null)).StatusCode);

        var bad = await a.PostAsync("/api/v1/auth/reauth", new { password = "otra contraseña larga 1", code = a.NextCode() });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        await a.ReauthAsync();
        var r = await a.PostAsync("/api/v1/exports", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("alvarez-finanzas-export", text);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(a.TotpSecret, text);

        app.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsync("/api/v1/exports", null)).StatusCode);
    }

    [Fact]
    public async Task Changing_password_revokes_all_sessions()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        await a.ReauthAsync();
        var shortPwd = await a.PostAsync("/api/v1/auth/password/change", new { currentPassword = a.Password, newPassword = "corta" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPwd.StatusCode);
        var r = await a.PostAsync("/api/v1/auth/password/change", new { currentPassword = a.Password, newPassword = "otra frase todavía más larga 456" });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/v1/entries")).StatusCode);
        var c = app.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await c.LoginAsync("augusto", "otra frase todavía más larga 456")).StatusCode);
    }

    [Fact]
    public async Task Unknown_json_properties_are_rejected_no_mass_assignment()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var r = await a.PostAsync("/api/v1/entries", new { kind = "income", concept = "x", amountMinor = "100", currency = "ARS", date = "2026-10-01", ownerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Html_in_concept_is_stored_and_returned_as_plain_text()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        const string payload = "<img src=x onerror=alert(1)>";
        var e = await a.PostJson("/api/v1/entries", new { kind = "expense", concept = payload, amountMinor = "100", currency = "ARS", date = "2026-10-01" });
        Assert.Equal(payload, e.Str("concept"));
    }
}
