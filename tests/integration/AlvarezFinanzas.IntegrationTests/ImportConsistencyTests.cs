using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlvarezFinanzas.Application;
using AlvarezFinanzas.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlvarezFinanzas.IntegrationTests;

/// <summary>
/// Restauración de exportaciones v2 editadas a mano o fabricadas: las mismas reglas de moneda, tarjetas,
/// transferencias y pagos que los endpoints normales. Un archivo incoherente se rechaza entero y no escribe nada.
/// </summary>
public class ImportConsistencyTests(PostgresFixture pg)
{
    /// <summary>Exportación v2 válida y coherente, con datos ficticios.</summary>
    private const string Valid = """
        {"format":"alvarez-finanzas-export","version":2,"exportedAt":"2026-10-20T00:00:00Z",
         "accounts":[
           {"id":"acc-ars","name":"Banco","kind":"bank","currency":"ARS","openingBalanceMinor":"0","openingDate":"2026-10-01"},
           {"id":"acc-usd","name":"Caja USD","kind":"cash","currency":"USD","openingBalanceMinor":"0","openingDate":"2026-10-01"},
           {"id":"acc-visa","name":"Visa","kind":"credit_card","currency":"ARS"}],
         "categories":[{"id":"cat-1","name":"Hogar","kind":"any"}],
         "entries":[
           {"id":"ent-1","kind":"expense","concept":"Cena","amountMinor":"5000","currency":"ARS","date":"2026-10-06","accountId":"acc-visa","categoryId":"cat-1"},
           {"id":"ent-2","kind":"income","concept":"Sueldo","amountMinor":"100000","currency":"ARS","date":"2026-10-05","accountId":"acc-ars"}],
         "plans":[{"id":"plan-1","kind":"card","concept":"Monitor","totalMinor":"30000","count":3,"currency":"ARS","firstDueDate":"2026-10-10",
           "accountId":"acc-visa","paidBeforeCount":0,"remainingOnly":false,
           "installments":[{"number":1,"amountMinor":"10000","dueDate":"2026-10-10"},{"number":2,"amountMinor":"10000","dueDate":"2026-11-10"},{"number":3,"amountMinor":"10000","dueDate":"2026-12-10"}]}],
         "payments":[{"id":"pay-1","sourceAccountId":"acc-ars","date":"2026-10-20","currency":"ARS","amountMinor":"15000",
           "allocations":[{"entryId":"ent-1","amountMinor":"5000"},{"planId":"plan-1","installmentNumber":1,"amountMinor":"10000"}]}],
         "transfers":[{"id":"tr-1","fromAccountId":"acc-ars","toAccountId":"acc-usd","currency":"ARS","amountMinor":"100","date":"2026-10-07"}]}
        """;

    private static JsonObject Doc() => JsonNode.Parse(Valid)!.AsObject();

    private static JsonObject At(JsonObject doc, string list, int index) => doc[list]![index]!.AsObject();

    private static Dictionary<string, string> Key(string k) => new() { ["Idempotency-Key"] = k };

    private static async Task<HttpResponseMessage> PreviewAsync(Client c, string json) => await c.PostAsync("/api/v1/imports/preview", json);

    [Fact]
    public async Task Coherent_export_restores_and_crafted_inconsistent_files_are_rejected_whole()
    {
        // Muchas vistas previas seguidas: se amplía solo el cupo horario de importación de esta prueba.
        await using var app = await TestApp.StartAsync(pg, new Dictionary<string, string> { ["App:ImportExportPerHour"] = "100" });
        var a = await app.SignedInAsync();

        // La transferencia de la plantilla cruza monedas: sin ella el archivo es coherente.
        var coherent = Doc();
        coherent["transfers"] = new JsonArray();
        var ok = await PreviewAsync(a, coherent.ToJsonString());
        Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

        var cases = new List<(string name, Action<JsonObject> mutate, string fragment)>
        {
            ("transferencia ARS hacia cuenta USD", _ => { }, "monedas distintas"),
            ("transferencia USD hacia una tarjeta", d => { d["transfers"] = new JsonArray(JsonNode.Parse("""{"id":"tr-2","fromAccountId":"acc-usd","toAccountId":"acc-visa","currency":"USD","amountMinor":"100","date":"2026-10-07"}""")); }, "tarjeta de crédito"),
            ("transferencia a la misma cuenta", d => { At(d, "transfers", 0)["toAccountId"] = "acc-ars"; }, "distintos"),
            ("ingreso USD en cuenta ARS", d => { At(d, "entries", 1)["currency"] = "USD"; }, "otra moneda"),
            ("plan USD en tarjeta ARS", d => { At(d, "plans", 0)["currency"] = "USD"; }, "otra moneda"),
            ("pago USD desde cuenta ARS", d => { At(d, "payments", 0)["currency"] = "USD"; }, "cuenta de origen es de otra moneda"),
            ("pago desde una tarjeta", d => { At(d, "payments", 0)["sourceAccountId"] = "acc-visa"; }, "caja, banco o billetera"),
            ("préstamo: capital en plan de tarjeta", d => { At(d, "plans", 0)["disbursementMinor"] = "1000"; At(d, "plans", 0)["disbursementDate"] = "2026-10-01"; }, "solo aplica a préstamos"),
            ("cuenta de acreditación sin capital", d => { At(d, "plans", 0)["disbursementAccountId"] = "acc-ars"; }, "requieren el capital"),
            ("ingreso en tarjeta", d => { At(d, "entries", 1)["accountId"] = "acc-visa"; }, "ingreso no puede"),
            ("tarjeta con saldo inicial", d => { At(d, "accounts", 2)["openingBalanceMinor"] = "100"; At(d, "accounts", 2)["openingDate"] = "2026-10-01"; }, "saldo inicial"),
            ("fecha de apertura sin saldo", d => { At(d, "accounts", 0).Remove("openingBalanceMinor"); }, "van juntos"),
            ("cuenta que no está en el archivo", d => { At(d, "entries", 1)["accountId"] = "acc-x"; }, "no está en el archivo"),
            ("pago de gasto que no es de tarjeta", d => { At(d, "entries", 0)["accountId"] = "acc-ars"; }, "cargados a una tarjeta"),
            ("pago mayor que lo pendiente", d => { At(d, "payments", 0)["amountMinor"] = "16000"; At(d, "payments", 0)["allocations"]![0]!["amountMinor"] = "6000"; }, "superan lo pendiente"),
            ("pago que no suma sus asignaciones", d => { At(d, "payments", 0)["amountMinor"] = "15001"; }, "no coincide con la suma"),
            ("cuota pagada inexistente", d => { At(d, "payments", 0)["allocations"]![1]!["installmentNumber"] = 4; }, "no existe en su plan"),
            ("dos cuentas activas homónimas", d => { At(d, "accounts", 1)["name"] = "Banco"; At(d, "accounts", 1)["currency"] = "ARS"; d["transfers"] = new JsonArray(); }, "mismo nombre"),
            ("identificador con separadores", d => { At(d, "payments", 0)["allocations"]![1]!["planId"] = "plan:1"; }, "identificador"),
        };
        foreach (var (name, mutate, fragment) in cases)
        {
            var doc = Doc();
            mutate(doc);
            var r = await PreviewAsync(a, doc.ToJsonString());
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {(int)r.StatusCode} {body}");
            var errors = (await r.Json()).GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.True(errors.Any(e => e.Contains(fragment, StringComparison.OrdinalIgnoreCase)), $"{name}: {string.Join(" | ", errors)}");
        }

        // Nada se escribió: ni cuentas, ni movimientos, ni planes.
        Assert.Empty((await a.GetJson("/api/v1/accounts?includeDeleted=true")).EnumerateArray());
        Assert.Equal(0, (await a.GetJson("/api/v1/entries")).GetProperty("total").GetInt32());

        // El archivo coherente sí se restaura y los saldos salen por moneda, sin mezclar.
        var batch = (await ok.Json()).Str("batchId");
        await a.PostJson($"/api/v1/imports/{batch}/commit", new { confirm = true }, HttpStatusCode.OK, Key("restore-coherente-1"));
        var accounts = (await a.GetJson("/api/v1/accounts")).EnumerateArray().ToDictionary(x => x.Str("name"));
        Assert.Equal("85000", accounts["Banco"].Str("cashBalanceMinor"));    // 0 + 1.000,00 − 150,00
        Assert.Equal("0", accounts["Caja USD"].Str("cashBalanceMinor"));
        Assert.Equal("20000", accounts["Visa"].Str("pendingObligationsMinor")); // 300,00 + 50,00 − 150,00
    }

    /// <summary>Si los datos normalizados de la vista previa se alteran, la confirmación vuelve a validar y no escribe nada.</summary>
    [Theory]
    [InlineData("entry", "ent-2", "currency", "USD", "otra moneda")]
    [InlineData("plan", "plan-1", "disbursementMinor", "1000", "solo aplica a préstamos")]
    [InlineData("plan", "plan-1", "installmentAmounts", "30000,0,0", "no cumplen las reglas")]
    public async Task Commit_revalidates_and_never_returns_500(string target, string sourceId, string field, string value, string fragment)
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var doc = Doc();
        doc["transfers"] = new JsonArray();
        var preview = await PreviewAsync(a, doc.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var batchId = Guid.Parse((await preview.Json()).Str("batchId"));

        await using (var db = app.NewDbContext())
        {
            var batch = await db.ImportBatches.IgnoreQueryFilters([AppDbContext.OwnerFilter]).SingleAsync(b => b.Id == batchId);
            var records = JsonNode.Parse(batch.NormalizedPayload!)!.AsArray();
            var record = records.Single(r => r!["target"]!.GetValue<string>() == target && r["sourceId"]!.GetValue<string>() == sourceId)!;
            record["fields"]![field] = value;
            if (field == "disbursementMinor") record["fields"]!["disbursementDate"] = "2026-10-01";
            batch.NormalizedPayload = records.ToJsonString();
            await db.SaveChangesAsync();
        }

        var commit = await a.PostAsync($"/api/v1/imports/{batchId}/commit", new { confirm = true }, Key("restore-alterado-1"));
        var body = await commit.Content.ReadAsStringAsync();
        Assert.True(commit.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)commit.StatusCode} {body}");
        Assert.Contains(fragment, (await commit.Json()).Str("title"), StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await a.GetJson("/api/v1/accounts?includeDeleted=true")).EnumerateArray());
        Assert.Equal(0, (await a.GetJson("/api/v1/entries")).GetProperty("total").GetInt32());
        Assert.Empty((await a.GetJson("/api/v1/plans")).EnumerateArray());
    }

    [Fact]
    public async Task Legacy_preview_always_warns_that_the_file_has_no_balances_payments_or_due_dates()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        const string onlyEntries = """
            {"version":1,"records":[
              {"id":"r1","type":"income","name":"Sueldo","amount":2100000,"currency":"ARS","date":"2026-01-05","category":"Trabajo","account":"Banco","count":1},
              {"id":"r2","type":"expense","name":"Súper","amount":86500,"currency":"ARS","date":"2026-01-05","category":"Alimentación","account":"Banco","count":1}
            ]}
            """;
        var r = await PreviewAsync(a, onlyEntries);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var warnings = (await r.Json()).GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains(ImportParser.LegacyLimitsWarning, warnings);
        Assert.Contains(warnings, w => w!.Contains("saldos") && w.Contains("pagos") && w.Contains("vencimientos"));
    }
}
