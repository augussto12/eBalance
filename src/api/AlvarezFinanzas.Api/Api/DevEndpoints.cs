using AlvarezFinanzas.Application;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;

namespace AlvarezFinanzas.Api;

/// <summary>
/// Datos ficticios por acción explícita, solo en Development con App:EnableDemoData=true. En producción el endpoint no existe.
/// </summary>
public static class DevEndpoints
{
    public sealed record DemoRequest(string? Month, string? Currency);

    public static void Map(RouteGroupBuilder api, IWebHostEnvironment env, AppOptions options)
    {
        if (!env.IsDevelopment() || !options.EnableDemoData) return;
        api.MapPost("/dev/demo", Demo);
    }

    private static async Task<IResult> Demo(DemoRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var v = new Validation();
        if (!FinanceCalendar.TryParseMonth(req.Month, out var month)) v.Add("month", "Mes no válido.");
        var currency = v.Currency("currency", req.Currency);
        if (!v.IsValid) return v.Problem();
        var owner = ctx.User.UserId();
        var day5 = new DateOnly(month.Year, month.Month, 5);

        Account Acc(string name, AccountKind kind)
        {
            var existing = db.Accounts.FirstOrDefault(a => a.OwnerId == owner && a.Name == name && a.Currency == currency && a.DeletedAt == null);
            if (existing is not null) return existing;
            var a = new Account { OwnerId = owner, Name = name, Kind = kind, Currency = currency };
            db.Accounts.Add(a);
            return a;
        }
        Category Cat(string name)
        {
            var existing = db.Categories.FirstOrDefault(c => c.OwnerId == owner && c.Name == name && c.DeletedAt == null);
            if (existing is not null) return existing;
            var c = new Category { OwnerId = owner, Name = name, Kind = CategoryKind.Any };
            db.Categories.Add(c);
            return c;
        }

        var bank = Acc("Banco · ejemplo", AccountKind.Bank);
        var cash = Acc("Efectivo · ejemplo", AccountKind.Cash);
        var debit = Acc("Débito · ejemplo", AccountKind.Bank);
        var visa = Acc("Visa · ejemplo", AccountKind.CreditCard);
        (EntryKind kind, string name, long amount, string cat, Account acc)[] entries =
        [
            (EntryKind.Income, "Sueldo · ejemplo", 2_100_000_00, "Trabajo", bank),
            (EntryKind.Expense, "Supermercado · ejemplo", 86_500_00, "Alimentación", debit),
            (EntryKind.Expense, "Internet · ejemplo", 32_000_00, "Servicios", bank),
            (EntryKind.Expense, "Café y merienda · ejemplo", 12_500_00, "Ocio", cash),
        ];
        foreach (var (kind, name, amount, cat, acc) in entries)
        {
            db.Entries.Add(new Entry { OwnerId = owner, Kind = kind, Concept = name, AmountMinor = amount, Currency = currency, EffectiveDate = day5, Account = acc, AccountId = acc.Id, Category = Cat(cat) });
        }
        foreach (var (kind, name, total, cat, acc) in new[] { (PlanKind.Card, "Monitor · ejemplo", 480_000_00L, "Tecnología", visa), (PlanKind.Loan, "Préstamo · ejemplo", 720_000_00L, "Otros", bank) })
        {
            var plan = new InstallmentPlan
            {
                OwnerId = owner, Kind = kind, Concept = name, TotalMinor = total, Count = 6, Currency = currency, FirstDueDate = day5,
                AccountId = acc.Id, Account = acc, Category = Cat(cat),
            };
            plan.Installments = PlanSchedule.Build(plan);
            db.Plans.Add(plan);
        }
        audit.Add(db, "dev.demo", "success");
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new { created = entries.Length + 2 });
    }
}
