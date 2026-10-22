using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlvarezFinanzas.Application;

/// <summary>
/// Ocurrencia presupuestaria de un mes: un ingreso/gasto simple en su fecha o una cuota en su vencimiento.
/// La compra financiada nunca se asigna completa al inicio.
/// </summary>
public sealed record Occurrence(
    string Source, Guid Id, Guid? PlanId, EntryKind Kind, string Concept, long AmountMinor, DateOnly Date,
    string? CategoryName, string? AccountName, int? InstallmentNumber, int? InstallmentCount, PlanKind? PlanKind,
    long PendingMinor, bool DeclaredPaid, long Version, DateOnly? PlanStart);

public sealed record MonthTotals(DateOnly Month, long IncomeMinor, long ExpenseMinor, long InstallmentsMinor,
    long InstallmentsPendingMinor, long InstallmentsPaidMinor)
{
    /// <summary>Balance presupuestario: ingresos − gastos asignados − cuotas. No es saldo bancario disponible.</summary>
    public long BalanceMinor => checked(IncomeMinor - ExpenseMinor);
}

public sealed class ReportService(AppDbContext db)
{
    public async Task<List<Occurrence>> OccurrencesAsync(Guid owner, Currency currency, DateOnly from, DateOnly toInclusive, CancellationToken ct)
    {
        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.OwnerId == owner && e.DeletedAt == null && e.Currency == currency
                        && e.EffectiveDate >= from && e.EffectiveDate <= toInclusive)
            .Select(e => new Occurrence("entry", e.Id, null, e.Kind, e.Concept, e.AmountMinor, e.EffectiveDate,
                e.Category != null ? e.Category.Name : null, e.Account != null ? e.Account.Name : null,
                null, null, null, e.AmountMinor - e.PaidMinor, false, e.Version, null))
            .ToListAsync(ct);

        var installments = await db.Installments.AsNoTracking()
            .Where(i => i.OwnerId == owner && i.Plan!.DeletedAt == null && i.Plan.Currency == currency
                        && i.DueDate >= from && i.DueDate <= toInclusive)
            .Select(i => new Occurrence("installment", i.Id, i.PlanId, EntryKind.Expense, i.Plan!.Concept, i.AmountMinor, i.DueDate,
                i.Plan.Category != null ? i.Plan.Category.Name : null, i.Plan.Account != null ? i.Plan.Account.Name : null,
                i.Number, i.Plan.Count, i.Plan.Kind, i.DeclaredPaid ? 0 : i.AmountMinor - i.PaidMinor, i.DeclaredPaid, i.Plan.Version, i.Plan.FirstDueDate))
            .ToListAsync(ct);

        entries.AddRange(installments);
        return entries;
    }

    public static MonthTotals Totals(DateOnly month, IEnumerable<Occurrence> all)
    {
        long income = 0, expense = 0, inst = 0, pending = 0, paid = 0;
        foreach (var o in all)
        {
            if (o.Date.Year != month.Year || o.Date.Month != month.Month) continue;
            if (o.Kind == EntryKind.Income) income = checked(income + o.AmountMinor);
            else expense = checked(expense + o.AmountMinor);
            if (o.Source == "installment")
            {
                inst = checked(inst + o.AmountMinor);
                pending = checked(pending + o.PendingMinor);
                paid = checked(paid + o.AmountMinor - o.PendingMinor);
            }
        }
        return new MonthTotals(month, income, expense, inst, pending, paid);
    }

    public async Task<(List<MonthTotals> Months, List<Occurrence> Occurrences)> RangeAsync(Guid owner, Currency currency, DateOnly startMonth, int months, CancellationToken ct)
    {
        var start = FinanceCalendar.MonthIndex(startMonth);
        var end = FinanceCalendar.FirstOfMonthIndex(start + months).AddDays(-1);
        var occ = await OccurrencesAsync(owner, currency, FinanceCalendar.FirstOfMonthIndex(start), end, ct);
        var result = Enumerable.Range(start, months).Select(i => Totals(FinanceCalendar.FirstOfMonthIndex(i), occ)).ToList();
        return (result, occ);
    }
}

public sealed record AccountBalance(long? CashBalanceMinor, long? PendingObligationsMinor);

/// <summary>
/// Saldo de caja = apertura + flujos registrados desde la fecha de apertura. Sin apertura, no se informa saldo:
/// no se muestra un «disponible» supuesto con datos incompletos.
/// </summary>
public sealed class BalanceService(AppDbContext db)
{
    public async Task<Dictionary<Guid, AccountBalance>> ComputeAsync(Guid owner, IReadOnlyList<Account> accounts, CancellationToken ct)
    {
        var result = new Dictionary<Guid, AccountBalance>();
        var ids = accounts.Select(a => a.Id).ToList();
        if (ids.Count == 0) return result;

        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.OwnerId == owner && e.DeletedAt == null && e.AccountId != null && ids.Contains(e.AccountId.Value))
            .Select(e => new { AccountId = e.AccountId!.Value, e.Kind, e.AmountMinor, e.PaidMinor, e.EffectiveDate })
            .ToListAsync(ct);
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.OwnerId == owner && p.DeletedAt == null && ids.Contains(p.SourceAccountId))
            .Select(p => new { p.SourceAccountId, p.AmountMinor, p.Date })
            .ToListAsync(ct);
        var transfers = await db.Transfers.AsNoTracking()
            .Where(t => t.OwnerId == owner && t.DeletedAt == null && (ids.Contains(t.FromAccountId) || ids.Contains(t.ToAccountId)))
            .Select(t => new { t.FromAccountId, t.ToAccountId, t.AmountMinor, t.Date })
            .ToListAsync(ct);
        var disbursements = await db.Plans.AsNoTracking()
            .Where(p => p.OwnerId == owner && p.DeletedAt == null && p.DisbursementAccountId != null && ids.Contains(p.DisbursementAccountId.Value))
            .Select(p => new { AccountId = p.DisbursementAccountId!.Value, Amount = p.DisbursementMinor!.Value, Date = p.DisbursementDate!.Value })
            .ToListAsync(ct);
        var cardInstallments = await db.Installments.AsNoTracking()
            .Where(i => i.OwnerId == owner && i.Plan!.DeletedAt == null && i.Plan.AccountId != null && ids.Contains(i.Plan.AccountId.Value) && !i.DeclaredPaid)
            .Select(i => new { AccountId = i.Plan!.AccountId!.Value, Pending = i.AmountMinor - i.PaidMinor })
            .ToListAsync(ct);

        foreach (var a in accounts)
        {
            if (a.Kind == AccountKind.CreditCard)
            {
                var pending = Money.Sum(cardInstallments.Where(x => x.AccountId == a.Id).Select(x => x.Pending))
                              + Money.Sum(entries.Where(x => x.AccountId == a.Id && x.Kind == EntryKind.Expense).Select(x => x.AmountMinor - x.PaidMinor));
                result[a.Id] = new AccountBalance(null, pending);
                continue;
            }
            if (a.OpeningBalanceMinor is not { } opening || a.OpeningDate is not { } from)
            {
                result[a.Id] = new AccountBalance(null, null);
                continue;
            }
            var balance = opening;
            foreach (var e in entries.Where(x => x.AccountId == a.Id && x.EffectiveDate >= from))
                balance = e.Kind == EntryKind.Income ? checked(balance + e.AmountMinor) : checked(balance - e.AmountMinor);
            foreach (var p in payments.Where(x => x.SourceAccountId == a.Id && x.Date >= from))
                balance = checked(balance - p.AmountMinor);
            foreach (var t in transfers.Where(x => x.Date >= from))
            {
                if (t.FromAccountId == a.Id) balance = checked(balance - t.AmountMinor);
                if (t.ToAccountId == a.Id) balance = checked(balance + t.AmountMinor);
            }
            foreach (var d in disbursements.Where(x => x.AccountId == a.Id && x.Date >= from))
                balance = checked(balance + d.Amount);
            result[a.Id] = new AccountBalance(balance, null);
        }
        return result;
    }
}

public static class PlanSchedule
{
    /// <summary>Genera cuotas exactas con fechas ancladas al día original de la primera cuota.</summary>
    public static List<Installment> Build(InstallmentPlan plan)
    {
        var amounts = Money.Distribute(plan.TotalMinor, plan.Count);
        var list = new List<Installment>(plan.Count);
        for (var i = 0; i < plan.Count; i++)
        {
            list.Add(new Installment
            {
                OwnerId = plan.OwnerId,
                PlanId = plan.Id,
                Number = i + 1,
                AmountMinor = amounts[i],
                DueDate = FinanceCalendar.DueDate(plan.FirstDueDate, i + 1),
                DeclaredPaid = i < plan.PaidBeforeCount,
            });
        }
        return list;
    }
}
