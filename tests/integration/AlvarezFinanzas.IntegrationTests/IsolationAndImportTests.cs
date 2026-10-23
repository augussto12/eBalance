using System.Net;
using Xunit;

namespace AlvarezFinanzas.IntegrationTests;

/// <summary>Autorización por propietario con dos usuarios aunque no exista registro público.</summary>
public class IsolationTests(PostgresFixture pg)
{
    [Fact]
    public async Task User_b_cannot_read_modify_relate_or_aggregate_user_a_records()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync("augusto");
        var b = await app.SignedInAsync("intruso", "otra frase larga para el intruso");

        var accA = await a.AccountAsync("Banco A");
        var catA = (await a.PostJson("/api/v1/categories", new { name = "Privada" })).Str("id");
        var entryA = (await a.PostJson("/api/v1/entries", new { kind = "income", concept = "Sueldo A", amountMinor = "500000", currency = "ARS", date = "2026-10-05", accountId = accA })).Str("id");
        var planA = await a.PostJson("/api/v1/plans", new { kind = "card", concept = "Plan A", amountMode = "total", amountMinor = "60000", count = 3, currency = "ARS", firstDueDate = "2026-10-05" });
        var instA = planA.GetProperty("installments")[0].Str("id");

        // Lectura directa por ID → 404 sin confirmar existencia.
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/entries/{entryA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/plans/{planA.Str("id")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/plans/{planA.Str("id")}/installments")).StatusCode);
        // Modificación / borrado / restauración.
        Assert.Equal(HttpStatusCode.NotFound, (await b.PatchAsync($"/api/v1/entries/{entryA}", new { kind = "income", concept = "hack", amountMinor = "1", currency = "ARS", date = "2026-10-05", version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/v1/entries/{entryA}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PatchAsync($"/api/v1/accounts/{accA}", new { name = "hack", version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/v1/plans/{planA.Str("id")}?version=1")).StatusCode);
        // Relaciones: no puede usar la cuenta/categoría de A.
        var rel = await b.PostAsync("/api/v1/entries", new { kind = "expense", concept = "x", amountMinor = "100", currency = "ARS", date = "2026-10-05", accountId = accA, categoryId = Guid.Parse(catA) });
        Assert.Equal(HttpStatusCode.BadRequest, rel.StatusCode);
        var accB = await b.AccountAsync("Banco B");
        var pay = await b.PostAsync("/api/v1/payments", new { sourceAccountId = accB, date = "2026-10-06", currency = "ARS", amountMinor = "20000",
            allocations = new[] { new { installmentId = Guid.Parse(instA), amountMinor = "20000" } } }, new Dictionary<string, string> { ["Idempotency-Key"] = "ataque-0001" });
        // Igual que una cuenta ajena: «no existe» por campo (400), sin confirmar que la cuota es de otro usuario.
        Assert.Equal(HttpStatusCode.BadRequest, pay.StatusCode);
        var tr = await b.PostAsync("/api/v1/transfers", new { fromAccountId = accB, toAccountId = accA, currency = "ARS", amountMinor = "100", date = "2026-10-06" });
        Assert.Equal(HttpStatusCode.BadRequest, tr.StatusCode);
        // Listas y agregaciones.
        Assert.Equal(0, (await b.GetJson("/api/v1/entries")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await b.GetJson("/api/v1/plans")).GetArrayLength());
        Assert.Single((await b.GetJson("/api/v1/accounts")).EnumerateArray());
        Assert.Equal("0", (await b.GetJson("/api/v1/dashboard?month=2026-10&currency=ARS")).GetProperty("totals").Str("incomeMinor"));
        Assert.Equal(0, (await b.GetJson("/api/v1/movements?month=2026-10&currency=ARS&q=Sueldo")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await b.GetJson($"/api/v1/obligations?planId={planA.Str("id")}&currency=ARS")).GetArrayLength());
        // Exportación de B no incluye datos de A.
        await b.ReauthAsync();
        var export = await (await b.PostAsync("/api/v1/exports", null)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Sueldo A", export);
        Assert.DoesNotContain(accA.ToString(), export);
        // Sesiones de A no son visibles ni revocables por B.
        var sessionsA = await a.GetJson("/api/v1/auth/sessions");
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/v1/auth/sessions/{sessionsA[0].Str("id")}")).StatusCode);
        // A sigue intacta.
        Assert.Equal("Sueldo A", (await a.GetJson($"/api/v1/entries/{entryA}")).Str("concept"));
    }

    [Fact]
    public async Task Database_rejects_cross_owner_references_even_bypassing_the_api()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync("augusto");
        var b = await app.SignedInAsync("intruso", "otra frase larga para el intruso");
        var accA = await a.AccountAsync("Banco A");
        var entryB = (await b.PostJson("/api/v1/entries", new { kind = "expense", concept = "x", amountMinor = "100", currency = "ARS", date = "2026-10-05" })).Str("id");
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(app.Services);
        var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Infrastructure.AppDbContext>(scope.ServiceProvider);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlInterpolatedAsync(db.Database,
            $"UPDATE entries SET account_id = {accA} WHERE id = {Guid.Parse(entryB)}"));
        Assert.Contains("fk_entries_accounts", ex.Message + ex.InnerException?.Message);
    }
}

public class ImportTests(PostgresFixture pg)
{
    private const string Legacy = """
        {"version":1,"records":[
          {"id":"r1","type":"income","name":"Sueldo","amount":2100000,"currency":"ARS","date":"2026-01-05","category":"Trabajo","account":"Banco","count":1},
          {"id":"r2","type":"expense","name":"<b>Súper</b>","amount":86500.5,"currency":"ARS","date":"2026-01-05","category":"Alimentación","account":"Débito","count":1},
          {"id":"r3","type":"card","name":"Monitor","amount":480000,"currency":"ARS","date":"2026-01-05","category":"Tecnología","account":"Visa","count":6},
          {"id":"r4","type":"expense","name":"Hosting","amount":10,"currency":"USD","date":"2026-01-05","category":"Trabajo","account":"Banco","count":1}
        ]}
        """;

    private static async Task<System.Text.Json.JsonElement> PreviewAsync(Client c, string json, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var r = await c.PostAsync("/api/v1/imports/preview", json);
        Assert.True(r.StatusCode == expected, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return await r.Json();
    }

    [Fact]
    public async Task Legacy_import_preview_commit_is_idempotent_and_detects_conflicts()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var preview = await PreviewAsync(a, Legacy);
        Assert.Equal(4, preview.GetProperty("summary").GetProperty("new").GetInt32());
        Assert.Contains(preview.GetProperty("accountsToCreate").EnumerateArray(), x => x.Str("key") == "Banco|USD");
        Assert.Contains(preview.GetProperty("accountsToCreate").EnumerateArray(), x => x.Str("key") == "Banco|ARS");
        var batch = preview.Str("batchId");
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "import-legacy-0001" };
        var commit = await a.PostJson($"/api/v1/imports/{batch}/commit", new { confirm = true, accountKinds = new Dictionary<string, string> { ["Visa|ARS"] = "credit_card" } }, HttpStatusCode.OK, headers);
        Assert.Equal(4, commit.GetProperty("result").GetProperty("created").GetInt32());
        // Reintento con la misma clave: misma respuesta, sin duplicar.
        await a.PostJson($"/api/v1/imports/{batch}/commit", new { confirm = true }, HttpStatusCode.OK, headers);
        Assert.Equal(3, (await a.GetJson("/api/v1/entries")).GetProperty("total").GetInt32());

        var accounts = (await a.GetJson("/api/v1/accounts")).EnumerateArray().ToList();
        Assert.Equal(2, accounts.Count(x => x.Str("name") == "Banco")); // homónimas ARS/USD separadas
        Assert.Equal("credit_card", accounts.Single(x => x.Str("name") == "Visa").Str("kind"));
        var dash = await a.GetJson("/api/v1/dashboard?month=2026-01&currency=ARS");
        Assert.Equal("8000000", dash.GetProperty("totals").Str("installmentsMinor"));
        Assert.Equal("8000000", dash.GetProperty("totals").Str("installmentsPendingMinor")); // no se marca pagada aunque sea pasada
        var entries = (await a.GetJson("/api/v1/entries?q=S%C3%BAper")).GetProperty("items");
        Assert.Equal("<b>Súper</b>", entries[0].Str("concept"));
        Assert.Equal("8650050", entries[0].Str("amountMinor"));

        // Misma carga otra vez → todo duplicado. Contenido cambiado → conflicto visible y sin sobrescribir.
        var again = await PreviewAsync(a, Legacy);
        Assert.Equal(4, again.GetProperty("summary").GetProperty("duplicates").GetInt32());
        var changed = Legacy.Replace("\"amount\":2100000", "\"amount\":2200000");
        var conflict = await PreviewAsync(a, changed);
        Assert.Equal(1, conflict.GetProperty("summary").GetProperty("conflicts").GetInt32());
        var c2 = await a.PostJson($"/api/v1/imports/{conflict.Str("batchId")}/commit", new { confirm = true }, HttpStatusCode.OK,
            new Dictionary<string, string> { ["Idempotency-Key"] = "import-legacy-0002" });
        Assert.Equal(0, c2.GetProperty("result").GetProperty("created").GetInt32());
        Assert.Equal("210000000", (await a.GetJson("/api/v1/dashboard?month=2026-01&currency=ARS")).GetProperty("totals").Str("incomeMinor"));
    }

    [Fact]
    public async Task Invalid_import_writes_nothing()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bad = Legacy.Replace("86500.5", "86500.505");
        var r = await PreviewAsync(a, bad, HttpStatusCode.UnprocessableEntity);
        Assert.Contains(r.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("decimales"));
        Assert.Equal(0, (await a.GetJson("/api/v1/entries")).GetProperty("total").GetInt32());
        Assert.Empty((await a.GetJson("/api/v1/accounts")).EnumerateArray());
        var notJson = await a.SendAsync(HttpMethod.Post, "/api/v1/imports/preview", null);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, notJson.StatusCode);
        var tooBig = await a.PostAsync("/api/v1/imports/preview", "{\"version\":1,\"records\":[\"" + new string('x', 6 * 1024 * 1024) + "\"]}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
    }

    [Fact]
    public async Task Commit_requires_confirmation_and_owner()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var b = await app.SignedInAsync("intruso", "otra frase larga para el intruso");
        var preview = await PreviewAsync(a, Legacy);
        var batch = preview.Str("batchId");
        var noConfirm = await a.PostAsync($"/api/v1/imports/{batch}/commit", new { confirm = false }, new Dictionary<string, string> { ["Idempotency-Key"] = "k-00000001" });
        Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        var foreign = await b.PostAsync($"/api/v1/imports/{batch}/commit", new { confirm = true }, new Dictionary<string, string> { ["Idempotency-Key"] = "k-00000002" });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        app.Clock.Advance(TimeSpan.FromMinutes(31));
        // Sesión de A expiró por inactividad: volver a entrar y verificar que la vista previa caducó.
        var a2 = app.NewClient();
        a2.TotpSecret = a.TotpSecret;
        await a2.LoginAsync("augusto", a.Password);
        await a2.PostAsync("/api/v1/auth/mfa/verify", new { code = a2.NextCode() });
        await a2.RefreshCsrfAsync();
        var expired = await a2.PostAsync($"/api/v1/imports/{batch}/commit", new { confirm = true }, new Dictionary<string, string> { ["Idempotency-Key"] = "k-00000003" });
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
    }

    [Fact]
    public async Task Export_then_restore_into_another_owner_reproduces_totals()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var visa = await a.AccountAsync("Visa", "credit_card");
        await a.PostJson("/api/v1/entries", new { kind = "income", concept = "Sueldo", amountMinor = "210000000", currency = "ARS", date = "2026-10-05", accountId = bank });
        await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Cena", amountMinor = "5000000", currency = "ARS", date = "2026-10-06", accountId = visa });
        await a.PostJson("/api/v1/plans", new { kind = "card", concept = "Monitor", amountMode = "total", amountMinor = "10000", count = 3, currency = "ARS", firstDueDate = "2026-10-05", accountId = visa });
        await a.PostJson("/api/v1/payments", new { sourceAccountId = bank, targetAccountId = visa, date = "2026-10-20", currency = "ARS", amountMinor = "5003334" },
            HttpStatusCode.Created, new Dictionary<string, string> { ["Idempotency-Key"] = "pago-export-0001" });
        await a.ReauthAsync();
        var export = await (await a.PostAsync("/api/v1/exports", null)).Content.ReadAsStringAsync();

        var b = await app.SignedInAsync("restaurado", "otra frase larga para restaurar");
        var preview = await PreviewAsync(b, export);
        Assert.Equal(0, preview.GetProperty("summary").GetProperty("conflicts").GetInt32());
        await b.PostJson($"/api/v1/imports/{preview.Str("batchId")}/commit", new { confirm = true }, HttpStatusCode.OK,
            new Dictionary<string, string> { ["Idempotency-Key"] = "restore-0001" });
        var da = await a.GetJson("/api/v1/dashboard?month=2026-10&currency=ARS");
        var db = await b.GetJson("/api/v1/dashboard?month=2026-10&currency=ARS");
        Assert.Equal(da.GetProperty("totals").ToString(), db.GetProperty("totals").ToString());
        var obligationsB = (await b.GetJson("/api/v1/accounts")).EnumerateArray().Single(x => x.Str("name") == "Visa");
        Assert.Equal("6666", obligationsB.Str("pendingObligationsMinor"));
    }
}
