using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using OtpNet;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(AlvarezFinanzas.IntegrationTests.PostgresFixture))]

namespace AlvarezFinanzas.IntegrationTests;

/// <summary>PostgreSQL real y efímero (no EF InMemory): constraints, transacciones y FKs compuestas reales.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-alpine3.24").Build();
    public string AdminConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var conn = new NpgsqlConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn)) await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name }.ConnectionString;
    }
}

public sealed class TestApp(string connectionString, IDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    public const string Origin = "https://localhost";
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "alvarez-keys-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:App", connectionString);
        builder.UseSetting("App:PublicOrigin", Origin);
        builder.UseSetting("App:DataProtectionApplicationName", "alvarez-finanzas-tests");
        builder.UseSetting("App:KeyRingPath", _keys);
        foreach (var (k, v) in settings ?? new Dictionary<string, string>()) builder.UseSetting(k, v);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
        });
    }

    public static async Task<TestApp> StartAsync(PostgresFixture pg, IDictionary<string, string>? settings = null)
    {
        var app = new TestApp(await pg.CreateDatabaseAsync(), settings);
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        return app;
    }

    public async Task CreateUserAsync(string username, string password)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var r = await users.CreateAsync(new AppUser { UserName = username, DisplayName = username + " Test", CreatedAt = DateTimeOffset.UtcNow, LockoutEnabled = true }, password);
        Assert.True(r.Succeeded, string.Join(";", r.Errors.Select(e => e.Description)));
    }

    public Client NewClient()
    {
        var cookies = new CookieContainer();
        return new Client(this, CreateDefaultClient(new Uri(Origin), new CookieContainerHandler(cookies)), cookies);
    }

    /// <summary>Crea usuario, inicia sesión y completa el alta TOTP. Devuelve un cliente con sesión completa.</summary>
    public async Task<Client> SignedInAsync(string username = "augusto", string password = "una frase larga de prueba 123")
    {
        await CreateUserAsync(username, password);
        var c = NewClient();
        await c.LoginAsync(username, password);
        var start = await c.PostAsync("/api/v1/auth/mfa/enroll/start", null);
        start.EnsureSuccessStatusCode();
        c.TotpSecret = (await start.Json()).GetProperty("secret").GetString()!;
        var confirm = await c.PostAsync("/api/v1/auth/mfa/enroll/confirm", new { code = c.NextCode() });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        c.RecoveryCodes = (await confirm.Json()).GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        await c.RefreshCsrfAsync();
        c.Password = password;
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_keys, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class Client(TestApp app, HttpClient http, CookieContainer cookies)
{
    public HttpClient Http { get; } = http;
    public CookieContainer Cookies { get; } = cookies;
    public string? Csrf { get; set; }
    public string TotpSecret { get; set; } = "";
    public string Password { get; set; } = "";
    public List<string> RecoveryCodes { get; set; } = [];
    public string OriginHeader { get; set; } = TestApp.Origin;

    public string NextCode()
    {
        // Avanza el reloj un paso TOTP para no reutilizar el mismo timestep.
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        return new Totp(Base32Encoding.ToBytes(TotpSecret)).ComputeTotp(app.Clock.GetUtcNow().UtcDateTime);
    }

    public string CurrentCode() => new Totp(Base32Encoding.ToBytes(TotpSecret)).ComputeTotp(app.Clock.GetUtcNow().UtcDateTime);

    public async Task RefreshCsrfAsync()
    {
        var r = await Http.GetAsync("/api/v1/auth/csrf");
        r.EnsureSuccessStatusCode();
        Csrf = (await r.Json()).GetProperty("token").GetString();
    }

    public async Task<HttpResponseMessage> LoginAsync(string username, string password)
    {
        await RefreshCsrfAsync();
        var r = await PostAsync("/api/v1/auth/login", new { username, password });
        await RefreshCsrfAsync();
        return r;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => Http.GetAsync(url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body, IDictionary<string, string>? headers = null) => SendAsync(HttpMethod.Post, url, body, headers);
    public Task<HttpResponseMessage> PatchAsync(string url, object? body) => SendAsync(HttpMethod.Patch, url, body);
    public Task<HttpResponseMessage> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url, null);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, IDictionary<string, string>? headers = null, bool csrf = true)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is string raw) req.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        else if (body is not null) req.Content = JsonContent.Create(body);
        if (csrf && Csrf is not null) req.Headers.Add("X-CSRF-TOKEN", Csrf);
        if (OriginHeader.Length > 0) req.Headers.Add("Origin", OriginHeader);
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) req.Headers.TryAddWithoutValidation(k, v);
        return Http.SendAsync(req);
    }

    public async Task<JsonElement> GetJson(string url)
    {
        var r = await Http.GetAsync(url);
        Assert.True(r.IsSuccessStatusCode, $"{url} → {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return await r.Json();
    }

    public async Task<JsonElement> PostJson(string url, object body, HttpStatusCode expected = HttpStatusCode.Created, IDictionary<string, string>? headers = null)
    {
        var r = await PostAsync(url, body, headers);
        Assert.True(r.StatusCode == expected, $"{url} → {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return await r.Json();
    }

    public async Task ReauthAsync()
    {
        var r = await PostAsync("/api/v1/auth/reauth", new { password = Password, code = NextCode() });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await RefreshCsrfAsync();
    }

    public async Task<Guid> AccountAsync(string name, string kind = "bank", string currency = "ARS") =>
        (await PostJson("/api/v1/accounts", new { name, kind, currency })).GetProperty("id").GetGuid();
}

public static class HttpExtensions
{
    public static async Task<JsonElement> Json(this HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? "" : e.GetProperty(name).ToString();
}
