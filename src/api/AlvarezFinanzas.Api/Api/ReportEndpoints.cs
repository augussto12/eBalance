using AlvarezFinanzas.Application;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;

namespace AlvarezFinanzas.Api;

/// <summary>Resumen, proyección y lista mensual: cálculo exacto en servidor, siempre por una sola moneda.</summary>
public static class ReportEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", Dashboard);
        api.MapGet("/projection", Projection);
        api.MapGet("/movements", Movements);
        api.MapGet("/trash", Trash);
    }

    private static object TotalsDto(MonthTotals t) => new
    {
        month = FinanceCalendar.FormatMonth(t.Month),
        incomeMinor = Money.ToApi(t.IncomeMinor),
        expenseMinor = Money.ToApi(t.ExpenseMinor),
        installmentsMinor = Money.ToApi(t.InstallmentsMinor),
        installmentsPendingMinor = Money.ToApi(t.InstallmentsPendingMinor),
        installmentsPaidMinor = Money.ToApi(t.InstallmentsPaidMinor),
        balanceMinor = t.BalanceMinor.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static object OccurrenceDto(Occurrence o) => new
    {
        source = o.Source,
        id = o.Id,
        planId = o.PlanId,
        kind = o.Kind.Api(),
        planKind = o.PlanKind?.Api(),
        concept = o.Concept,
        amountMinor = Money.ToApi(o.AmountMinor),
        pendingMinor = Money.ToApi(o.PendingMinor),
        date = o.Date.Api(),
        planStart = o.PlanStart.Api(),
        categoryName = o.CategoryName,
        accountName = o.AccountName,
        installmentNumber = o.InstallmentNumber,
        installmentCount = o.InstallmentCount,
        declaredPaid = o.DeclaredPaid,
        version = o.Version,
    };

    private static bool ParseCommon(Validation v, string? month, string? currency, out DateOnly m, out Currency c)
    {
        if (!FinanceCalendar.TryParseMonth(month, out m)) v.Add("month", "Mes no válido (AAAA-MM).");
        c = v.Currency("currency", currency);
        return v.IsValid;
    }

    private static async Task<IResult> Dashboard(HttpContext ctx, ReportService reports, string? month, string? currency, CancellationToken ct)
    {
        var v = new Validation();
        if (!ParseCommon(v, month, currency, out var m, out var c)) return v.Problem();
        var owner = ctx.User.UserId();
        var start = FinanceCalendar.FirstOfMonthIndex(FinanceCalendar.MonthIndex(m) - 5);
        var (months, occ) = await reports.RangeAsync(owner, c, start, 6, ct);
        var current = months[^1];
        var monthOcc = occ.Where(o => o.Date.Year == m.Year && o.Date.Month == m.Month).ToList();
        var categories = monthOcc.Where(o => o.Kind == EntryKind.Expense)
            .GroupBy(o => o.CategoryName ?? "Sin categoría")
            .Select(g => new { name = g.Key, amount = Money.Sum(g.Select(x => x.AmountMinor)) })
            .OrderByDescending(x => x.amount).ThenBy(x => x.name)
            .Take(5)
            .Select(x => new { x.name, amountMinor = Money.ToApi(x.amount) });
        var recent = monthOcc.OrderByDescending(o => o.Date).ThenBy(o => o.Concept).Take(5).Select(OccurrenceDto);
        return TypedResults.Ok(new
        {
            month = FinanceCalendar.FormatMonth(m),
            currency = c.ToString(),
            totals = TotalsDto(current),
            series = months.Select(TotalsDto),
            categories,
            recent,
        });
    }

    private static async Task<IResult> Projection(HttpContext ctx, ReportService reports, string? startMonth, int? months, string? currency, CancellationToken ct)
    {
        var v = new Validation();
        if (!ParseCommon(v, startMonth, currency, out var m, out var c)) return v.Problem();
        var count = months ?? 12;
        if (count is < 1 or > 24) { v.Add("months", "Entre 1 y 24 meses."); return v.Problem(); }
        var (list, _) = await reports.RangeAsync(ctx.User.UserId(), c, m, count, ct);
        return TypedResults.Ok(new { currency = c.ToString(), months = list.Select(TotalsDto) });
    }

    private static async Task<IResult> Movements(HttpContext ctx, ReportService reports, string? month, string? currency, string? q, string? type,
        int? page, int? pageSize, CancellationToken ct)
    {
        var v = new Validation();
        if (!ParseCommon(v, month, currency, out var m, out var c)) return v.Problem();
        if (q is { Length: > 90 }) v.Add("q", "Búsqueda demasiado larga.");
        if (type is not (null or "all" or "income" or "expense" or "card" or "loan")) v.Add("type", "Filtro no válido.");
        if (!v.IsValid) return v.Problem();
        var occ = await reports.OccurrencesAsync(ctx.User.UserId(), c, m, m.AddMonths(1).AddDays(-1), ct);
        IEnumerable<Occurrence> rows = occ;
        rows = type switch
        {
            "income" => rows.Where(o => o.Kind == EntryKind.Income),
            "expense" => rows.Where(o => o.Kind == EntryKind.Expense && o.Source == "entry"),
            "card" => rows.Where(o => o.PlanKind == PlanKind.Card),
            "loan" => rows.Where(o => o.PlanKind == PlanKind.Loan),
            _ => rows,
        };
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            rows = rows.Where(o => Contains(o.Concept, needle) || Contains(o.CategoryName, needle) || Contains(o.AccountName, needle));
        }
        var ordered = rows.OrderByDescending(o => o.Date).ThenBy(o => o.Concept).ToList();
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 50, 1, 100);
        var items = ordered.Skip((p - 1) * size).Take(size).Select(OccurrenceDto).ToList();
        return TypedResults.Ok(new PageResult<object>(items, p, size, ordered.Count));
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && System.Globalization.CultureInfo.GetCultureInfo("es-AR").CompareInfo
            .IndexOf(haystack, needle, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>Papelera: registros eliminados de forma reversible.</summary>
    private static async Task<IResult> Trash(HttpContext ctx, AppDbContext db, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var entries = await db.Entries.AsNoTracking().Where(e => e.OwnerId == owner && e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt).Take(100)
            .Select(e => new { type = "entry", id = e.Id, concept = e.Concept, currency = e.Currency.ToString(), amountMinor = e.AmountMinor, date = e.EffectiveDate, deletedAt = e.DeletedAt })
            .ToListAsync(ct);
        var plans = await db.Plans.AsNoTracking().Where(p => p.OwnerId == owner && p.DeletedAt != null)
            .OrderByDescending(p => p.DeletedAt).Take(100)
            .Select(p => new { type = p.Kind == PlanKind.Card ? "card" : "loan", id = p.Id, concept = p.Concept, currency = p.Currency.ToString(), amountMinor = p.TotalMinor, date = p.FirstDueDate, deletedAt = p.DeletedAt })
            .ToListAsync(ct);
        var transfers = await db.Transfers.AsNoTracking().Where(t => t.OwnerId == owner && t.DeletedAt != null)
            .OrderByDescending(t => t.DeletedAt).Take(100)
            .Select(t => new { type = "transfer", id = t.Id, concept = "Transferencia", currency = t.Currency.ToString(), amountMinor = t.AmountMinor, date = t.Date, deletedAt = t.DeletedAt })
            .ToListAsync(ct);
        var all = entries.Concat(plans).Concat(transfers).OrderByDescending(x => x.deletedAt)
            .Select(x => new { x.type, x.id, x.concept, x.currency, amountMinor = Money.ToApi(x.amountMinor), date = x.date.Api(), x.deletedAt });
        return TypedResults.Ok(all);
    }
}
