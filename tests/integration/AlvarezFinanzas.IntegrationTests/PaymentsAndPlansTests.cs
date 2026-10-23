using System.Net;
using System.Text.Json;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlvarezFinanzas.IntegrationTests;

/// <summary>
/// Pagos, historial y reversión; edición de cronograma (con y sin pagos revertidos), ajuste de pendientes,
/// restauración con versión y caso de docs/05 «pagos parciales y adelantados no se pierden al cambiar de mes».
/// </summary>
public class PaymentsAndPlansTests(PostgresFixture pg)
{
    private static Dictionary<string, string> Key(string k) => new() { ["Idempotency-Key"] = k };

    private static DateOnly Today(TestApp app) => FinanceCalendar.Today(app.Clock, FinanceCalendar.ArgentinaZone);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static object PlanBody(Guid? account, string total, int count, string first, long? version = null, string kind = "card", string concept = "Heladera") =>
        new { kind, concept, amountMode = "total", amountMinor = total, count, currency = "ARS", firstDueDate = first, accountId = account, version };

    private static List<JsonElement> Installments(JsonElement plan) => plan.GetProperty("installments").EnumerateArray().ToList();

    private static async Task<JsonElement> PayInstallmentAsync(Client a, Guid source, string installmentId, string amount, string date, string key) =>
        await a.PostJson("/api/v1/payments", new
        {
            sourceAccountId = source, date, currency = "ARS", amountMinor = amount,
            allocations = new[] { new { installmentId = Guid.Parse(installmentId), amountMinor = amount } },
        }, HttpStatusCode.Created, Key(key));

    /// <summary>Nueva sesión del mismo usuario (la anterior caduca por inactividad al adelantar el reloj).</summary>
    private static async Task<Client> ReloginAsync(TestApp app, Client previous, string username = "augusto")
    {
        var c = app.NewClient();
        c.TotpSecret = previous.TotpSecret;
        c.Password = previous.Password;
        await c.LoginAsync(username, previous.Password);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/v1/auth/mfa/verify", new { code = c.NextCode() })).StatusCode);
        await c.RefreshCsrfAsync();
        return c;
    }

    [Fact]
    public async Task Schedule_edit_without_payments_rebuilds_installments_exactly()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var visa = await a.AccountAsync("Visa", "credit_card");
        var plan = await a.PostJson("/api/v1/plans", PlanBody(visa, "30000", 3, "2027-01-31"));
        var id = plan.Str("id");
        var oldIds = Installments(plan).Select(i => i.Str("id")).ToList();

        var grown = await a.PatchAsync($"/api/v1/plans/{id}", PlanBody(visa, "40000", 4, "2028-01-31", version: 1));
        Assert.Equal(HttpStatusCode.OK, grown.StatusCode);
        var g = await grown.Json();
        Assert.Equal(2, g.GetProperty("version").GetInt64());
        Assert.Equal("40000", g.Str("totalMinor"));
        var inst = Installments(g);
        Assert.Equal([1, 2, 3, 4], inst.Select(i => i.GetProperty("number").GetInt32()));
        Assert.All(inst, i => Assert.Equal("10000", i.Str("amountMinor")));
        Assert.Equal(["2028-01-31", "2028-02-29", "2028-03-31", "2028-04-30"], inst.Select(i => i.Str("dueDate")));
        // Las cuotas que siguen existiendo conservan su fila.
        Assert.Equal(oldIds, inst.Take(3).Select(i => i.Str("id")));

        var shrunk = await a.PatchAsync($"/api/v1/plans/{id}", PlanBody(visa, "10000", 3, "2028-01-31", version: 2));
        Assert.Equal(HttpStatusCode.OK, shrunk.StatusCode);
        Assert.Equal(["3334", "3333", "3333"], Installments(await shrunk.Json()).Select(i => i.Str("amountMinor")));
        var dash = await a.GetJson("/api/v1/dashboard?month=2028-04&currency=ARS");
        Assert.Equal("0", dash.GetProperty("totals").Str("installmentsMinor"));
        var stale = await a.PatchAsync($"/api/v1/plans/{id}", PlanBody(visa, "50000", 5, "2028-01-31", version: 2));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Schedule_edit_after_reversed_payment_succeeds_and_keeps_the_reversed_payment()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var visa = await a.AccountAsync("Visa", "credit_card");
        var plan = await a.PostJson("/api/v1/plans", PlanBody(visa, "30000", 3, "2027-01-10"));
        var id = plan.Str("id");
        var inst = Installments(plan);
        var payment = await a.PostJson("/api/v1/payments", new
        {
            sourceAccountId = bank, date = "2027-01-05", currency = "ARS", amountMinor = "20000",
            allocations = new[]
            {
                new { installmentId = Guid.Parse(inst[0].Str("id")), amountMinor = "10000" },
                new { installmentId = Guid.Parse(inst[2].Str("id")), amountMinor = "10000" },
            },
        }, HttpStatusCode.Created, Key("pago-revertido-0001"));

        // Con pago vigente: bloqueado, con código para que la UI ofrezca la lista de pagos.
        var blocked = await a.PatchAsync($"/api/v1/plans/{id}", PlanBody(visa, "20000", 2, "2027-01-10", version: 1));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("payments_applied", (await blocked.Json()).Str("code"));

        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync($"/api/v1/payments/{payment.Str("id")}?version=1")).StatusCode);
        var afterReverse = await a.GetJson($"/api/v1/plans/{id}");
        Assert.False(afterReverse.GetProperty("hasPayments").GetBoolean());
        var version = afterReverse.GetProperty("version").GetInt64();

        // Antes: 500 por la FK de las asignaciones del pago revertido. Ahora: 200 y la cuota 3 desaparece.
        var edit = await a.PatchAsync($"/api/v1/plans/{id}", PlanBody(visa, "20000", 2, "2027-02-10", version: version));
        Assert.True(edit.StatusCode == HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());
        var edited = await edit.Json();
        Assert.Equal(["10000", "10000"], Installments(edited).Select(i => i.Str("amountMinor")));
        Assert.Equal(["2027-02-10", "2027-03-10"], Installments(edited).Select(i => i.Str("dueDate")));
        Assert.All(Installments(edited), i => Assert.Equal("pending", i.Str("status")));

        // Crecer de nuevo también funciona.
        var grow = await a.PatchAsync($"/api/v1/plans/{id}", PlanBody(visa, "40000", 4, "2027-02-10", version: edited.GetProperty("version").GetInt64()));
        Assert.Equal(HttpStatusCode.OK, grow.StatusCode);

        // El pago revertido sigue en el historial (fila y auditoría), sin su asignación a la cuota que dejó de existir.
        Assert.Equal(0, (await a.GetJson($"/api/v1/payments?planId={id}")).GetArrayLength());
        var history = await a.GetJson($"/api/v1/payments?planId={id}&includeReversed=true");
        var reversed = Assert.Single(history.EnumerateArray());
        Assert.Equal(payment.Str("id"), reversed.Str("id"));
        Assert.NotEqual(JsonValueKind.Null, reversed.GetProperty("reversedAt").ValueKind);
        var allocation = Assert.Single(reversed.GetProperty("allocations").EnumerateArray());
        Assert.Equal(1, allocation.GetProperty("installmentNumber").GetInt32());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var paymentId = payment.Str("id");
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "payment.create" && e.ResourceId == paymentId));
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "payment.reverse" && e.ResourceId == paymentId));
        Assert.True(await db.Payments.AnyAsync(p => p.Id == Guid.Parse(paymentId) && p.DeletedAt != null));
    }

    [Fact]
    public async Task Adjust_pending_redistributes_only_open_installments()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var plan = await a.PostJson("/api/v1/plans", PlanBody(bank, "30000", 3, "2027-01-10", kind: "loan", concept: "Préstamo"));
        var id = plan.Str("id");
        var inst = Installments(plan);
        await PayInstallmentAsync(a, bank, inst[0].Str("id"), "4000", "2027-01-05", "pago-parcial-ajuste-1");
        var version = (await a.GetJson($"/api/v1/plans/{id}")).GetProperty("version").GetInt64();

        var tooSmall = await a.PostAsync($"/api/v1/plans/{id}/adjust-pending", new { pendingTotalMinor = "1", version });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSmall.StatusCode);
        var stale = await a.PostAsync($"/api/v1/plans/{id}/adjust-pending", new { pendingTotalMinor = "25001", version = version + 7 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var r = await a.PostAsync($"/api/v1/plans/{id}/adjust-pending", new { pendingTotalMinor = "25001", version });
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var adjusted = await r.Json();
        var after = Installments(adjusted);
        // La cuota 1 tiene un pago parcial: no se toca. 250,01 entre dos cuotas abiertas, exacto: 125,01 + 125,00.
        Assert.Equal(["10000", "12501", "12500"], after.Select(i => i.Str("amountMinor")));
        Assert.Equal("4000", after[0].Str("paidMinor"));
        Assert.Equal("partial", after[0].Str("status"));
        Assert.Equal("35001", adjusted.Str("totalMinor"));
        Assert.Equal("31001", adjusted.Str("pendingMinor"));
        Assert.Equal(inst.Select(i => i.Str("id")), after.Select(i => i.Str("id")));
        Assert.Equal("12501", (await a.GetJson("/api/v1/projection?startMonth=2027-02&months=1&currency=ARS")).GetProperty("months")[0].Str("expenseMinor"));

        // Sin cuotas abiertas: 422 explícito.
        await PayInstallmentAsync(a, bank, inst[1].Str("id"), "1", "2027-01-06", "pago-parcial-ajuste-2");
        await PayInstallmentAsync(a, bank, inst[2].Str("id"), "1", "2027-01-06", "pago-parcial-ajuste-3");
        var v2 = (await a.GetJson($"/api/v1/plans/{id}")).GetProperty("version").GetInt64();
        var none = await a.PostAsync($"/api/v1/plans/{id}/adjust-pending", new { pendingTotalMinor = "100", version = v2 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
    }

    [Fact]
    public async Task Plan_delete_and_restore_check_the_version()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var plan = await a.PostJson("/api/v1/plans", PlanBody(null, "60000", 6, "2027-03-05"));
        var id = plan.Str("id");
        var deleted = await a.DeleteAsync($"/api/v1/plans/{id}?version=1");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var deletedVersion = (await deleted.Json()).GetProperty("version").GetInt64();
        Assert.Equal(2, deletedVersion);
        Assert.Equal("0", (await a.GetJson("/api/v1/dashboard?month=2027-03&currency=ARS")).GetProperty("totals").Str("installmentsMinor"));
        var trash = Assert.Single((await a.GetJson("/api/v1/trash")).EnumerateArray());
        Assert.Equal(deletedVersion, trash.GetProperty("version").GetInt64());

        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsync($"/api/v1/plans/{id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsync($"/api/v1/plans/{id}/restore?version=1", null)).StatusCode);
        var restored = await a.PostAsync($"/api/v1/plans/{id}/restore?version={deletedVersion}", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await restored.Json()).GetProperty("deletedAt").ValueKind);
        Assert.Equal("10000", (await a.GetJson("/api/v1/dashboard?month=2027-03&currency=ARS")).GetProperty("totals").Str("installmentsMinor"));
        Assert.Equal(6, Installments(await a.GetJson($"/api/v1/plans/{id}")).Count);
        Assert.Empty((await a.GetJson("/api/v1/trash")).EnumerateArray());
        // Restaurar algo que no está en la papelera: 404.
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsync($"/api/v1/plans/{id}/restore?version=3", null)).StatusCode);
    }

    [Fact]
    public async Task Restore_requires_the_current_version_for_entries_transfers_accounts_and_categories()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var wallet = await a.AccountAsync("Billetera", "wallet");
        var category = (await a.PostJson("/api/v1/categories", new { name = "Hogar" })).Str("id");
        var entry = (await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Pintura", amountMinor = "100", currency = "ARS", date = "2026-10-05" })).Str("id");
        var transfer = (await a.PostJson("/api/v1/transfers", new { fromAccountId = bank, toAccountId = wallet, currency = "ARS", amountMinor = "100", date = "2026-10-05" })).Str("id");

        foreach (var path in new[] { $"/api/v1/entries/{entry}", $"/api/v1/transfers/{transfer}", $"/api/v1/accounts/{wallet}", $"/api/v1/categories/{category}" })
        {
            var deleted = await a.DeleteAsync($"{path}?version=1");
            Assert.True(deleted.StatusCode == HttpStatusCode.OK, $"{path} → {(int)deleted.StatusCode}");
            var version = (await deleted.Json()).GetProperty("version").GetInt64();
            Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsync($"{path}/restore", null)).StatusCode);
            var stale = await a.PostAsync($"{path}/restore?version=1", null);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            // El 409 trae la versión actual para reintentar a sabiendas.
            Assert.Equal(version, (await stale.Json()).GetProperty("current").GetProperty("version").GetInt64());
            var ok = await a.PostAsync($"{path}/restore?version={version}", null);
            Assert.True(ok.StatusCode == HttpStatusCode.OK, $"{path} → {(int)ok.StatusCode}");
            Assert.Equal(version + 1, (await ok.Json()).GetProperty("version").GetInt64());
        }
        Assert.Equal(1, (await a.GetJson("/api/v1/entries")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await a.GetJson("/api/v1/transfers")).GetArrayLength());
        Assert.Equal(2, (await a.GetJson("/api/v1/accounts")).GetArrayLength());
        Assert.Single((await a.GetJson("/api/v1/categories")).EnumerateArray());

        // Otro usuario no puede restaurar ni confirmar existencia.
        var b = await app.SignedInAsync("intruso", "otra frase larga para el intruso");
        var deletedAgain = await (await a.DeleteAsync($"/api/v1/entries/{entry}?version=3")).Json();
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/v1/entries/{entry}/restore?version={deletedAgain.GetProperty("version").GetInt64()}", null)).StatusCode);
    }

    [Fact]
    public async Task Payment_history_filters_describe_allocations_and_stay_isolated()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var visa = await a.AccountAsync("Visa", "credit_card");
        var card = await a.PostJson("/api/v1/plans", PlanBody(visa, "30000", 3, "2027-01-10", concept: "Monitor"));
        var loan = await a.PostJson("/api/v1/plans", PlanBody(bank, "60000", 2, "2027-01-15", kind: "loan", concept: "Préstamo"));
        var dinner = (await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Cena", amountMinor = "5000", currency = "ARS", date = "2027-01-03", accountId = visa })).Str("id");

        var payCard = await PayInstallmentAsync(a, bank, Installments(card)[1].Str("id"), "10000", "2027-01-05", "pago-hist-0001");
        var payDinner = await a.PostJson("/api/v1/payments", new
        {
            sourceAccountId = bank, date = "2027-01-06", currency = "ARS", amountMinor = "2000",
            allocations = new[] { new { entryId = Guid.Parse(dinner), amountMinor = "2000" } },
        }, HttpStatusCode.Created, Key("pago-hist-0002"));
        var payLoan = await PayInstallmentAsync(a, bank, Installments(loan)[0].Str("id"), "30000", "2027-01-07", "pago-hist-0003");

        var created = payCard.GetProperty("allocations")[0];
        Assert.Equal("Monitor", created.Str("concept"));
        Assert.Equal(2, created.GetProperty("installmentNumber").GetInt32());
        Assert.Equal(3, created.GetProperty("installmentCount").GetInt32());
        Assert.Equal("card", created.Str("planKind"));
        Assert.Equal("2027-02-10", created.Str("dueDate"));
        Assert.Equal("Banco", payCard.Str("sourceAccountName"));

        static List<string> Ids(JsonElement list) => list.EnumerateArray().Select(p => p.Str("id")).ToList();
        Assert.Equal([payCard.Str("id")], Ids(await a.GetJson($"/api/v1/payments?planId={card.Str("id")}")));
        Assert.Equal([payCard.Str("id")], Ids(await a.GetJson($"/api/v1/payments?installmentId={Installments(card)[1].Str("id")}")));
        Assert.Empty(Ids(await a.GetJson($"/api/v1/payments?installmentId={Installments(card)[0].Str("id")}")));
        Assert.Equal([payDinner.Str("id")], Ids(await a.GetJson($"/api/v1/payments?entryId={dinner}")));
        Assert.Equal("Cena", (await a.GetJson($"/api/v1/payments?entryId={dinner}"))[0].GetProperty("allocations")[0].Str("concept"));
        Assert.Equal([payDinner.Str("id"), payCard.Str("id")], Ids(await a.GetJson("/api/v1/payments?kind=card&currency=ARS")));
        Assert.Equal([payDinner.Str("id"), payCard.Str("id")], Ids(await a.GetJson($"/api/v1/payments?targetAccountId={visa}")));
        Assert.Equal([payLoan.Str("id")], Ids(await a.GetJson("/api/v1/payments?kind=loan")));
        Assert.Empty(Ids(await a.GetJson("/api/v1/payments?currency=USD")));
        Assert.Equal(HttpStatusCode.BadRequest, (await a.GetAsync("/api/v1/payments?kind=otro")).StatusCode);

        // Revertir con versión vieja → 409; con la actual → 200; queda fuera de la lista por defecto.
        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync($"/api/v1/payments/{payDinner.Str("id")}?version=9")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync($"/api/v1/payments/{payDinner.Str("id")}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.DeleteAsync($"/api/v1/payments/{payDinner.Str("id")}?version=2")).StatusCode);
        Assert.Empty(Ids(await a.GetJson($"/api/v1/payments?entryId={dinner}")));
        Assert.Equal([payDinner.Str("id")], Ids(await a.GetJson($"/api/v1/payments?entryId={dinner}&includeReversed=true")));
        Assert.Equal("0", (await a.GetJson($"/api/v1/entries/{dinner}")).Str("paidMinor"));

        var b = await app.SignedInAsync("intruso", "otra frase larga para el intruso");
        Assert.Empty(Ids(await b.GetJson($"/api/v1/payments?planId={card.Str("id")}&includeReversed=true")));
        Assert.Empty(Ids(await b.GetJson("/api/v1/payments?includeReversed=true")));
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/v1/payments/{payCard.Str("id")}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Paid_records_point_to_their_payments_and_can_be_deleted_after_reversal()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var visa = await a.AccountAsync("Visa", "credit_card");
        var entry = await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Zapatillas", amountMinor = "9000", currency = "ARS", date = "2027-01-03", accountId = visa });
        var plan = await a.PostJson("/api/v1/plans", PlanBody(visa, "30000", 3, "2027-01-10"));
        await a.PostJson("/api/v1/payments", new { sourceAccountId = bank, targetAccountId = visa, date = "2027-01-04", currency = "ARS", amountMinor = "19000" },
            HttpStatusCode.Created, Key("pago-resumen-0001"));

        var entryDelete = await a.DeleteAsync($"/api/v1/entries/{entry.Str("id")}?version=2");
        Assert.Equal(HttpStatusCode.Conflict, entryDelete.StatusCode);
        Assert.Equal("payments_applied", (await entryDelete.Json()).Str("code"));
        var planVersion = (await a.GetJson($"/api/v1/plans/{plan.Str("id")}")).GetProperty("version").GetInt64();
        var planDelete = await a.DeleteAsync($"/api/v1/plans/{plan.Str("id")}?version={planVersion}");
        Assert.Equal("payments_applied", (await planDelete.Json()).Str("code"));
        var entryEdit = await a.PatchAsync($"/api/v1/entries/{entry.Str("id")}", new { kind = "expense", concept = "Zapatillas", amountMinor = "100", currency = "ARS", date = "2027-01-03", accountId = visa, version = 2 });
        Assert.Equal("payments_applied", (await entryEdit.Json()).Str("code"));

        // El mensaje ya no es un callejón sin salida: la lista de pagos del gasto lleva al pago, que se revierte.
        var payments = await a.GetJson($"/api/v1/payments?entryId={entry.Str("id")}");
        var payment = Assert.Single(payments.EnumerateArray());
        Assert.Equal(2, payment.GetProperty("allocations").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync($"/api/v1/payments/{payment.Str("id")}?version={payment.GetProperty("version").GetInt64()}")).StatusCode);
        var entryVersion = (await a.GetJson($"/api/v1/entries/{entry.Str("id")}")).GetProperty("version").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync($"/api/v1/entries/{entry.Str("id")}?version={entryVersion}")).StatusCode);
        planVersion = (await a.GetJson($"/api/v1/plans/{plan.Str("id")}")).GetProperty("version").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync($"/api/v1/plans/{plan.Str("id")}?version={planVersion}")).StatusCode);
    }

    /// <summary>docs/05: «Pagos parciales y adelantados no se pierden por cambiar mes».</summary>
    [Fact]
    public async Task Partial_and_advance_payments_are_not_lost_when_the_month_changes()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var today = Today(app);
        var month0 = new DateOnly(today.Year, today.Month, 1);
        string Month(int delta) => FinanceCalendar.FormatMonth(month0.AddMonths(delta));
        var bank = await a.AccountAsync("Banco");
        var visa = await a.AccountAsync("Visa", "credit_card");
        var plan = await a.PostJson("/api/v1/plans", PlanBody(visa, "30000", 3, Iso(month0.AddDays(9)), concept: "Notebook"));
        var inst = Installments(plan);
        await PayInstallmentAsync(a, bank, inst[0].Str("id"), "4000", Iso(today), "pago-parcial-mes-1");     // parcial, cuota del mes
        await PayInstallmentAsync(a, bank, inst[2].Str("id"), "10000", Iso(today), "pago-adelantado-mes-3"); // adelantado, cuota de dentro de 2 meses

        async Task AssertMonthsAsync(Client c)
        {
            var expected = new[] { ("4000", "6000"), ("0", "10000"), ("10000", "0") };
            for (var i = 0; i < 3; i++)
            {
                var t = (await c.GetJson($"/api/v1/dashboard?month={Month(i)}&currency=ARS")).GetProperty("totals");
                Assert.Equal("10000", t.Str("installmentsMinor"));      // la obligación sigue en su mes, aunque esté pagada
                Assert.Equal("10000", t.Str("expenseMinor"));           // el pago no suma otro gasto
                Assert.Equal(expected[i].Item1, t.Str("installmentsPaidMinor"));
                Assert.Equal(expected[i].Item2, t.Str("installmentsPendingMinor"));
                var row = Assert.Single((await c.GetJson($"/api/v1/movements?month={Month(i)}&currency=ARS&type=card")).GetProperty("items").EnumerateArray());
                Assert.Equal(expected[i].Item2, row.Str("pendingMinor"));
            }
            var projection = (await c.GetJson($"/api/v1/projection?startMonth={Month(0)}&months=4&currency=ARS")).GetProperty("months").EnumerateArray().ToList();
            Assert.Equal(["10000", "10000", "10000", "0"], projection.Select(m => m.Str("expenseMinor")));
            var obligations = (await c.GetJson($"/api/v1/obligations?planId={plan.Str("id")}&currency=ARS")).EnumerateArray().ToList();
            Assert.Equal([1, 2], obligations.Select(o => o.GetProperty("number").GetInt32()));
            Assert.Equal(["6000", "10000"], obligations.Select(o => o.Str("pendingMinor")));
            var p = await c.GetJson($"/api/v1/plans/{plan.Str("id")}");
            Assert.Equal("14000", p.Str("paidMinor"));
            Assert.Equal("16000", p.Str("pendingMinor"));
            Assert.Equal(2, (await c.GetJson($"/api/v1/payments?planId={plan.Str("id")}")).GetArrayLength());
        }

        await AssertMonthsAsync(a);
        // El calendario real pasa al mes siguiente y al otro: nada se recalcula ni se pierde.
        app.Clock.Advance(TimeSpan.FromDays(35));
        await AssertMonthsAsync(await ReloginAsync(app, a));
        app.Clock.Advance(TimeSpan.FromDays(31));
        var later = await ReloginAsync(app, a);
        await AssertMonthsAsync(later);
        var due = inst.Count(i => DateOnly.ParseExact(i.Str("dueDate"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) <= Today(app));
        Assert.True(due >= 2);
        Assert.Equal(due, (await later.GetJson($"/api/v1/plans/{plan.Str("id")}")).GetProperty("currentInstallment").GetInt32());
    }

    [Fact]
    public async Task Cash_balance_ignores_future_dated_flows_until_their_date_in_the_owner_calendar()
    {
        await using var app = await TestApp.StartAsync(pg);
        // 02:00 UTC = 23:00 del día anterior en Buenos Aires: el «hoy» del propietario no es el de UTC.
        app.Clock.SetUtcNow(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(2), TimeSpan.Zero));
        var a = await app.SignedInAsync();
        var today = Today(app);
        Assert.Equal(DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime).AddDays(-1), today);
        var bank = await a.AccountAsync("Banco");
        var wallet = await a.AccountAsync("Billetera", "wallet");
        var visa = await a.AccountAsync("Visa", "credit_card");
        await a.PatchAsync($"/api/v1/accounts/{bank}", new { name = "Banco", openingBalanceMinor = "100000", openingDate = Iso(today.AddDays(-10)), version = 1 });
        await a.PatchAsync($"/api/v1/accounts/{wallet}", new { name = "Billetera", openingBalanceMinor = "0", openingDate = Iso(today.AddDays(-10)), version = 1 });
        await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Hoy", amountMinor = "1000", currency = "ARS", date = Iso(today), accountId = bank });
        await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Mañana (ya es hoy en UTC)", amountMinor = "2000", currency = "ARS", date = Iso(today.AddDays(1)), accountId = bank });
        await a.PostJson("/api/v1/entries", new { kind = "income", concept = "Sueldo futuro", amountMinor = "50000", currency = "ARS", date = Iso(today.AddDays(30)), accountId = bank });
        await a.PostJson("/api/v1/transfers", new { fromAccountId = bank, toAccountId = wallet, currency = "ARS", amountMinor = "3000", date = Iso(today.AddDays(5)) });
        await a.PostJson("/api/v1/plans", PlanBody(visa, "4000", 1, Iso(today.AddDays(20))));
        await a.PostJson("/api/v1/payments", new { sourceAccountId = bank, targetAccountId = visa, date = Iso(today.AddDays(3)), currency = "ARS", amountMinor = "4000" },
            HttpStatusCode.Created, Key("pago-futuro-0001"));
        await a.PostJson("/api/v1/plans", new
        {
            kind = "loan", concept = "Préstamo", amountMode = "total", amountMinor = "12000", count = 2, currency = "ARS", firstDueDate = Iso(today.AddDays(40)),
            disbursementMinor = "10000", disbursementDate = Iso(today.AddDays(7)), disbursementAccountId = bank,
        });

        var accounts = (await a.GetJson("/api/v1/accounts")).EnumerateArray().ToDictionary(x => x.Str("name"));
        Assert.Equal("99000", accounts["Banco"].Str("cashBalanceMinor"));
        Assert.Equal(Iso(today), accounts["Banco"].Str("cashBalanceAsOf"));
        Assert.Equal("0", accounts["Billetera"].Str("cashBalanceMinor"));
        Assert.Equal("99000", (await a.GetJson($"/api/v1/accounts/{bank}")).Str("cashBalanceMinor"));

        app.Clock.Advance(TimeSpan.FromDays(40));
        var later = await ReloginAsync(app, a);
        accounts = (await later.GetJson("/api/v1/accounts")).EnumerateArray().ToDictionary(x => x.Str("name"));
        // 100.000 − 1.000 − 2.000 + 50.000 − 3.000 − 4.000 + 10.000
        Assert.Equal("150000", accounts["Banco"].Str("cashBalanceMinor"));
        Assert.Equal("3000", accounts["Billetera"].Str("cashBalanceMinor"));
    }

    [Fact]
    public async Task Cash_balance_is_null_without_opening_balance()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var cash = await a.AccountAsync("Efectivo", "cash");
        await a.PostJson("/api/v1/entries", new { kind = "income", concept = "Sueldo", amountMinor = "500000", currency = "ARS", date = "2026-10-01", accountId = bank });
        await a.PatchAsync($"/api/v1/accounts/{cash}", new { name = "Efectivo", openingBalanceMinor = "-2500", openingDate = "2026-10-01", version = 1 });
        var accounts = (await a.GetJson("/api/v1/accounts")).EnumerateArray().ToDictionary(x => x.Str("name"));
        // Sin saldo inicial no se inventa un «disponible», aunque haya movimientos registrados.
        Assert.Equal(JsonValueKind.Null, accounts["Banco"].GetProperty("cashBalanceMinor").ValueKind);
        Assert.Equal(JsonValueKind.Null, accounts["Banco"].GetProperty("cashBalanceAsOf").ValueKind);
        Assert.Equal("-2500", accounts["Efectivo"].Str("cashBalanceMinor"));
        Assert.Equal(JsonValueKind.Null, (await a.GetJson($"/api/v1/accounts/{bank}")).GetProperty("cashBalanceMinor").ValueKind);
    }

    /// <summary>Aunque una fila incoherente llegue a la base (sin pasar por la API), el saldo nunca suma otra moneda.</summary>
    [Fact]
    public async Task Balances_never_mix_currencies_even_with_incoherent_rows()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var visa = await a.AccountAsync("Visa", "credit_card");
        await a.PatchAsync($"/api/v1/accounts/{bank}", new { name = "Banco", openingBalanceMinor = "0", openingDate = "2026-10-01", version = 1 });
        var entry = Guid.Parse((await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Ok", amountMinor = "1000", currency = "ARS", date = "2026-10-02", accountId = bank })).Str("id"));
        var plan = Guid.Parse((await a.PostJson("/api/v1/plans", PlanBody(visa, "3000", 3, "2026-10-10"))).Str("id"));
        var loan = Guid.Parse((await a.PostJson("/api/v1/plans", PlanBody(null, "3000", 3, "2026-10-10", kind: "loan"))).Str("id"));
        Assert.Equal("-1000", (await a.GetJson($"/api/v1/accounts/{bank}")).Str("cashBalanceMinor"));

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Entries.SingleAsync(e => e.Id == entry)).Currency = Currency.USD;
            (await db.Plans.SingleAsync(p => p.Id == plan)).Currency = Currency.USD;
            // Cuenta de acreditación sin capital: la base lo admite; antes rompía el cálculo de saldos.
            (await db.Plans.SingleAsync(p => p.Id == loan)).DisbursementAccountId = bank;
            await db.SaveChangesAsync();
        }
        var accounts = (await a.GetJson("/api/v1/accounts")).EnumerateArray().ToDictionary(x => x.Str("name"));
        Assert.Equal("0", accounts["Banco"].Str("cashBalanceMinor"));
        Assert.Equal("0", accounts["Visa"].Str("pendingObligationsMinor"));
    }

    [Fact]
    public async Task Accounts_and_categories_can_be_read_by_id_only_by_their_owner()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        await a.PatchAsync($"/api/v1/accounts/{bank}", new { name = "Banco", openingBalanceMinor = "1000", openingDate = "2026-10-01", version = 1 });
        var category = (await a.PostJson("/api/v1/categories", new { name = "Salud", kind = "expense" })).Str("id");

        var acc = await a.GetJson($"/api/v1/accounts/{bank}");
        Assert.Equal("Banco", acc.Str("name"));
        Assert.Equal("1000", acc.Str("cashBalanceMinor"));
        Assert.Equal(2, acc.GetProperty("version").GetInt64());
        var cat = await a.GetJson($"/api/v1/categories/{category}");
        Assert.Equal("Salud", cat.Str("name"));
        Assert.Equal("expense", cat.Str("kind"));
        // Archivadas siguen siendo legibles por ID (para la papelera y el historial).
        await a.DeleteAsync($"/api/v1/categories/{category}?version=1");
        Assert.NotEqual(JsonValueKind.Null, (await a.GetJson($"/api/v1/categories/{category}")).GetProperty("deletedAt").ValueKind);

        var b = await app.SignedInAsync("intruso", "otra frase larga para el intruso");
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/accounts/{bank}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/categories/{category}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/v1/accounts/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Same_rule_returns_the_same_status_code_on_every_path()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var visa = await a.AccountAsync("Visa", "credit_card");

        // Saldo inicial en tarjeta: 400 con error de campo al crear y al editar.
        var create = await a.PostAsync("/api/v1/accounts", new { name = "Master", kind = "credit_card", currency = "ARS", openingBalanceMinor = "100", openingDate = "2026-10-01" });
        var update = await a.PatchAsync($"/api/v1/accounts/{visa}", new { name = "Visa", openingBalanceMinor = "100", openingDate = "2026-10-01", version = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.True((await update.Json()).GetProperty("errors").TryGetProperty("openingBalanceMinor", out _));
        Assert.True((await create.Json()).GetProperty("errors").TryGetProperty("openingBalanceMinor", out _));

        // Última cuota fuera de rango: 400 por campo en la vista previa y al crear.
        var preview = await a.PostAsync("/api/v1/plans/preview", new { amountMode = "total", amountMinor = "36000", count = 360, firstDueDate = "2190-01-01" });
        var plan = await a.PostAsync("/api/v1/plans", PlanBody(null, "36000", 360, "2190-01-01"));
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, plan.StatusCode);
        Assert.True((await preview.Json()).GetProperty("errors").TryGetProperty("firstDueDate", out _));

        // Pagar más que lo pendiente: 422 tanto con asignación automática como explícita.
        var p = await a.PostJson("/api/v1/plans", PlanBody(visa, "1000", 1, "2027-01-10"));
        var auto = await a.PostAsync("/api/v1/payments", new { sourceAccountId = bank, targetAccountId = visa, date = "2027-01-05", currency = "ARS", amountMinor = "1001" }, Key("pago-excede-auto-1"));
        var explicitAllocation = await a.PostAsync("/api/v1/payments", new
        {
            sourceAccountId = bank, date = "2027-01-05", currency = "ARS", amountMinor = "1001",
            allocations = new[] { new { installmentId = Guid.Parse(Installments(p)[0].Str("id")), amountMinor = "1001" } },
        }, Key("pago-excede-expl-1"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, auto.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, explicitAllocation.StatusCode);
    }
}
