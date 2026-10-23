using System.Net;
using System.Text.Json;
using Xunit;

namespace AlvarezFinanzas.IntegrationTests;

/// <summary>Casos de aceptación monetarios de docs/05 con datos ficticios.</summary>
public class FinanceTests(PostgresFixture pg)
{
    private static async Task<(Guid bank, Guid visa)> SeedAcceptanceAsync(Client a)
    {
        var bank = await a.AccountAsync("Banco");
        var debit = await a.AccountAsync("Débito");
        var visa = await a.AccountAsync("Visa", "credit_card");
        await a.PostJson("/api/v1/entries", new { kind = "income", concept = "Sueldo", amountMinor = "210000000", currency = "ARS", date = "2026-10-05", accountId = bank });
        await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Supermercado", amountMinor = "8650000", currency = "ARS", date = "2026-10-05", accountId = debit });
        await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Internet", amountMinor = "3200000", currency = "ARS", date = "2026-10-05", accountId = bank });
        await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Café", amountMinor = "1250000", currency = "ARS", date = "2026-10-05" });
        await a.PostJson("/api/v1/plans", new { kind = "card", concept = "Monitor", amountMode = "total", amountMinor = "48000000", count = 6, currency = "ARS", firstDueDate = "2026-10-05", accountId = visa });
        await a.PostJson("/api/v1/plans", new { kind = "loan", concept = "Préstamo", amountMode = "total", amountMinor = "72000000", count = 6, currency = "ARS", firstDueDate = "2026-10-05", accountId = bank });
        return (bank, visa);
    }

    [Fact]
    public async Task Acceptance_month_totals_and_following_months()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        await SeedAcceptanceAsync(a);

        var d = await a.GetJson("/api/v1/dashboard?month=2026-10&currency=ARS");
        var t = d.GetProperty("totals");
        Assert.Equal("210000000", t.Str("incomeMinor"));
        Assert.Equal("33100000", t.Str("expenseMinor"));
        Assert.Equal("20000000", t.Str("installmentsMinor"));
        Assert.Equal("176900000", t.Str("balanceMinor"));
        Assert.Equal(6, d.GetProperty("series").GetArrayLength());

        var p = await a.GetJson("/api/v1/projection?startMonth=2026-10&months=12&currency=ARS");
        var months = p.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal("33100000", months[0].Str("expenseMinor"));
        for (var i = 1; i < 6; i++) Assert.Equal("20000000", months[i].Str("expenseMinor"));
        for (var i = 6; i < 12; i++) Assert.Equal("0", months[i].Str("expenseMinor"));

        var plans = await a.GetJson("/api/v1/plans?kind=card");
        var plan = await a.GetJson($"/api/v1/plans/{plans[0].Str("id")}");
        var inst = plan.GetProperty("installments").EnumerateArray().Select(i => i.Str("amountMinor")).ToList();
        Assert.Equal(6, inst.Count);
        Assert.All(inst, x => Assert.Equal("8000000", x));
    }

    [Fact]
    public async Task Plan_preview_distributes_exactly_and_rejects_zero_installments()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var p = await a.PostJson("/api/v1/plans/preview", new { amountMode = "total", amountMinor = "10000", count = 3, firstDueDate = "2028-01-31" }, HttpStatusCode.OK);
        Assert.Equal(["3334", "3333", "3333"], p.GetProperty("installments").EnumerateArray().Select(i => i.Str("amountMinor")));
        Assert.Equal(["2028-01-31", "2028-02-29", "2028-03-31"], p.GetProperty("installments").EnumerateArray().Select(i => i.Str("dueDate")));
        var zero = await a.PostAsync("/api/v1/plans/preview", new { amountMode = "total", amountMinor = "1", count = 3, firstDueDate = "2028-01-31" });
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        var byInstallment = await a.PostJson("/api/v1/plans/preview", new { amountMode = "installment", amountMinor = "8000000", count = 6, firstDueDate = "2026-12-01" }, HttpStatusCode.OK);
        Assert.Equal("48000000", byInstallment.Str("totalMinor"));
    }

    [Fact]
    public async Task Card_payment_reduces_pending_without_adding_budget_expense_and_is_idempotent()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var (bank, visa) = await SeedAcceptanceAsync(a);
        await a.PatchAsync($"/api/v1/accounts/{bank}", new { name = "Banco", openingBalanceMinor = "0", openingDate = "2026-10-01", version = 1 });

        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "pago-visa-octubre-001" };
        var body = new { sourceAccountId = bank, targetAccountId = visa, date = "2026-10-20", currency = "ARS", amountMinor = "8000000" };
        var pay = await a.PostJson("/api/v1/payments", body, HttpStatusCode.Created, headers);
        var again = await a.PostJson("/api/v1/payments", body, HttpStatusCode.OK, headers);
        Assert.Equal(pay.Str("id"), again.Str("id"));
        var different = await a.PostAsync("/api/v1/payments", new { sourceAccountId = bank, targetAccountId = visa, date = "2026-10-20", currency = "ARS", amountMinor = "100" }, headers);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);

        var d = await a.GetJson("/api/v1/dashboard?month=2026-10&currency=ARS");
        Assert.Equal("33100000", d.GetProperty("totals").Str("expenseMinor"));
        Assert.Equal("8000000", d.GetProperty("totals").Str("installmentsPaidMinor"));

        var accounts = (await a.GetJson("/api/v1/accounts")).EnumerateArray().ToList();
        var bankDto = accounts.Single(x => x.Str("name") == "Banco");
        // 0 + 2.100.000 − 32.000 − 80.000 (pago) = 1.988.000
        Assert.Equal("198800000", bankDto.Str("cashBalanceMinor"));
        var visaDto = accounts.Single(x => x.Str("name") == "Visa");
        Assert.Equal("40000000", visaDto.Str("pendingObligationsMinor"));

        // Pago parcial deja pendiente; asignación que supera lo pendiente se rechaza.
        var obligations = await a.GetJson($"/api/v1/obligations?accountId={visa}&currency=ARS");
        var next = obligations[0];
        var partial = await a.PostJson("/api/v1/payments", new { sourceAccountId = bank, date = "2026-11-05", currency = "ARS", amountMinor = "3000000",
            allocations = new[] { new { installmentId = Guid.Parse(next.Str("id")), amountMinor = "3000000" } } }, HttpStatusCode.Created,
            new Dictionary<string, string> { ["Idempotency-Key"] = "pago-parcial-0002" });
        var over = await a.PostAsync("/api/v1/payments", new { sourceAccountId = bank, date = "2026-11-05", currency = "ARS", amountMinor = "5000001",
            allocations = new[] { new { installmentId = Guid.Parse(next.Str("id")), amountMinor = "5000001" } } },
            new Dictionary<string, string> { ["Idempotency-Key"] = "pago-excedido-0003" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);

        // Plan con pagos: no se puede cambiar el cronograma ni borrar sin revertir.
        var plans = await a.GetJson("/api/v1/plans?kind=card");
        var planId = plans[0].Str("id");
        var version = plans[0].GetProperty("version").GetInt64();
        var edit = await a.PatchAsync($"/api/v1/plans/{planId}", new { kind = "card", concept = "Monitor", amountMode = "total", amountMinor = "50000000", count = 6, currency = "ARS", firstDueDate = "2026-10-05", accountId = visa, version });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync($"/api/v1/plans/{planId}?version={version}")).StatusCode);

        // Revertir el pago parcial devuelve el pendiente.
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync($"/api/v1/payments/{partial.Str("id")}?version=1")).StatusCode);
        var after = await a.GetJson($"/api/v1/obligations?accountId={visa}&currency=ARS");
        Assert.Equal("8000000", after[0].Str("pendingMinor"));
    }

    [Fact]
    public async Task Transfers_have_zero_budget_effect_and_reject_cross_currency()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var wallet = await a.AccountAsync("Mercado Pago", "wallet");
        var usd = await a.AccountAsync("Caja USD", "cash", "USD");
        await a.PatchAsync($"/api/v1/accounts/{bank}", new { name = "Banco", openingBalanceMinor = "5000000", openingDate = "2026-10-01", version = 1 });
        await a.PatchAsync($"/api/v1/accounts/{wallet}", new { name = "Mercado Pago", openingBalanceMinor = "0", openingDate = "2026-10-01", version = 1 });

        await a.PostJson("/api/v1/transfers", new { fromAccountId = bank, toAccountId = wallet, currency = "ARS", amountMinor = "1000000", date = "2026-10-10" });
        var d = await a.GetJson("/api/v1/dashboard?month=2026-10&currency=ARS");
        Assert.Equal("0", d.GetProperty("totals").Str("incomeMinor"));
        Assert.Equal("0", d.GetProperty("totals").Str("expenseMinor"));
        var accounts = (await a.GetJson("/api/v1/accounts")).EnumerateArray().ToDictionary(x => x.Str("name"), x => x.Str("cashBalanceMinor"));
        Assert.Equal("4000000", accounts["Banco"]);
        Assert.Equal("1000000", accounts["Mercado Pago"]);

        var cross = await a.PostAsync("/api/v1/transfers", new { fromAccountId = bank, toAccountId = usd, currency = "ARS", amountMinor = "1000", date = "2026-10-10" });
        Assert.Equal(HttpStatusCode.BadRequest, cross.StatusCode);
    }

    [Fact]
    public async Task Currencies_are_never_mixed()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        await a.PostJson("/api/v1/entries", new { kind = "income", concept = "Sueldo", amountMinor = "100000", currency = "ARS", date = "2026-10-05" });
        await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Hosting", amountMinor = "10000", currency = "USD", date = "2026-10-05" });
        var ars = await a.GetJson("/api/v1/dashboard?month=2026-10&currency=ARS");
        var usd = await a.GetJson("/api/v1/dashboard?month=2026-10&currency=USD");
        Assert.Equal("0", ars.GetProperty("totals").Str("expenseMinor"));
        Assert.Equal("10000", usd.GetProperty("totals").Str("expenseMinor"));
        Assert.Equal("0", usd.GetProperty("totals").Str("incomeMinor"));
        var accountMismatch = await a.AccountAsync("Caja USD", "cash", "USD");
        var r = await a.PostAsync("/api/v1/entries", new { kind = "expense", concept = "x", amountMinor = "100", currency = "ARS", date = "2026-10-05", accountId = accountMismatch });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Concurrent_edits_get_409_not_silent_overwrite()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var e = await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Original", amountMinor = "100", currency = "ARS", date = "2026-10-05" });
        var id = e.Str("id");
        var first = await a.PatchAsync($"/api/v1/entries/{id}", new { kind = "expense", concept = "Edición A", amountMinor = "100", currency = "ARS", date = "2026-10-05", version = 1 });
        var second = await a.PatchAsync($"/api/v1/entries/{id}", new { kind = "expense", concept = "Edición B", amountMinor = "200", currency = "ARS", date = "2026-10-05", version = 1 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("Edición A", (await a.GetJson($"/api/v1/entries/{id}")).Str("concept"));
    }

    [Fact]
    public async Task Soft_delete_and_restore_with_trash()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var e = await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Borrable", amountMinor = "100", currency = "ARS", date = "2026-10-05" });
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync($"/api/v1/entries/{e.Str("id")}?version=1")).StatusCode);
        Assert.Equal(0, (await a.GetJson("/api/v1/entries")).GetProperty("total").GetInt32());
        var trash = await a.GetJson("/api/v1/trash");
        Assert.Equal(1, trash.GetArrayLength());
        // Restaurar exige la versión actual, igual que borrar (la papelera la informa).
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsync($"/api/v1/entries/{e.Str("id")}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync($"/api/v1/entries/{e.Str("id")}/restore?version={trash[0].GetProperty("version").GetInt64()}", null)).StatusCode);
        Assert.Equal(1, (await a.GetJson("/api/v1/entries")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Ongoing_loan_registers_declared_paid_without_inventing_payments()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var bank = await a.AccountAsync("Banco");
        var p = await a.PostJson("/api/v1/plans", new
        {
            kind = "loan", concept = "Préstamo en curso", amountMode = "installment", amountMinor = "1000000", count = 12, currency = "ARS",
            firstDueDate = "2026-01-10", accountId = bank, paidBeforeCount = 9, disbursementMinor = "9000000", disbursementDate = "2025-12-20", disbursementAccountId = bank,
        });
        Assert.Equal("12000000", p.Str("totalMinor"));
        Assert.Equal("3000000", p.Str("pendingMinor"));
        var inst = p.GetProperty("installments").EnumerateArray().ToList();
        Assert.All(inst.Take(9), i => Assert.Equal("declared_paid", i.Str("status")));
        Assert.All(inst.Skip(9), i => Assert.Equal("pending", i.Str("status")));
        Assert.Equal(0, (await a.GetJson("/api/v1/payments")).GetProperty("total").GetInt32());
        // El capital recibido no es ingreso presupuestario.
        var d = await a.GetJson("/api/v1/dashboard?month=2025-12&currency=ARS");
        Assert.Equal("0", d.GetProperty("totals").Str("incomeMinor"));
    }

    [Fact]
    public async Task Movements_search_by_concept_category_and_account()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var mp = await a.AccountAsync("Mercado Pago", "wallet");
        var cat = (await a.PostJson("/api/v1/categories", new { name = "Alimentación" })).Str("id");
        await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Supermercado", amountMinor = "100", currency = "ARS", date = "2026-10-05", categoryId = Guid.Parse(cat) });
        await a.PostJson("/api/v1/entries", new { kind = "expense", concept = "Nafta", amountMinor = "100", currency = "ARS", date = "2026-10-06", accountId = mp });
        Assert.Equal(1, (await a.GetJson("/api/v1/movements?month=2026-10&currency=ARS&q=super")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await a.GetJson("/api/v1/movements?month=2026-10&currency=ARS&q=alimentacion")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await a.GetJson("/api/v1/movements?month=2026-10&currency=ARS&q=pago")).GetProperty("total").GetInt32());
        // «%» se escapa en ILIKE: no actúa como comodín.
        Assert.Equal(0, (await a.GetJson("/api/v1/entries?q=%25")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await a.GetJson("/api/v1/entries?month=2026-10&pageSize=1")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await a.GetJson("/api/v1/entries?month=2026-10&pageSize=1")).GetProperty("items").GetArrayLength());
    }
}
