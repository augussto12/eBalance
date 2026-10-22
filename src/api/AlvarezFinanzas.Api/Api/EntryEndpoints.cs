using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;

namespace AlvarezFinanzas.Api;

public static class EntryEndpoints
{
    public sealed record EntryRequest(string? Kind, string? Concept, string? AmountMinor, string? Currency, string? Date,
        Guid? AccountId, Guid? CategoryId, string? Note, long? Version);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/entries");
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create);
        g.MapPatch("/{id:guid}", Update);
        g.MapDelete("/{id:guid}", Delete);
        g.MapPost("/{id:guid}/restore", Restore);
    }

    public static object Dto(Entry e) => new
    {
        id = e.Id,
        kind = e.Kind.Api(),
        concept = e.Concept,
        note = e.Note,
        amountMinor = Money.ToApi(e.AmountMinor),
        paidMinor = Money.ToApi(e.PaidMinor),
        currency = e.Currency.ToString(),
        date = e.EffectiveDate.Api(),
        accountId = e.AccountId,
        accountName = e.Account?.Name,
        categoryId = e.CategoryId,
        categoryName = e.Category?.Name,
        version = e.Version,
        deletedAt = e.DeletedAt,
    };

    private static async Task<IResult> List(HttpContext ctx, AppDbContext db, string? month, string? from, string? to, string? currency,
        string? kind, Guid? accountId, Guid? categoryId, string? q, bool? deleted, int? page, int? pageSize, CancellationToken ct)
    {
        var v = new Validation();
        var owner = ctx.User.UserId();
        var query = db.Entries.AsNoTracking().Include(e => e.Account).Include(e => e.Category).Where(e => e.OwnerId == owner);
        query = deleted == true ? query.Where(e => e.DeletedAt != null) : query.Where(e => e.DeletedAt == null);
        if (month is not null)
        {
            if (!FinanceCalendar.TryParseMonth(month, out var m)) v.Add("month", "Mes no válido (AAAA-MM).");
            else query = query.Where(e => e.EffectiveDate >= m && e.EffectiveDate < m.AddMonths(1));
        }
        if (from is not null) { var f = v.Date("from", from); query = query.Where(e => e.EffectiveDate >= f); }
        if (to is not null) { var t = v.Date("to", to); query = query.Where(e => e.EffectiveDate <= t); }
        if (currency is not null) { var c = v.Currency("currency", currency); query = query.Where(e => e.Currency == c); }
        if (kind is not null) { var k = v.Enum("kind", kind, ApiMaps.EntryKinds, "Tipo no válido."); query = query.Where(e => e.Kind == k); }
        if (accountId is not null) query = query.Where(e => e.AccountId == accountId);
        if (categoryId is not null) query = query.Where(e => e.CategoryId == categoryId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            if (q.Length > 90) v.Add("q", "Búsqueda demasiado larga.");
            var pattern = "%" + LikeEscape(q.Trim()) + "%";
            query = query.Where(e => EF.Functions.ILike(e.Concept, pattern, "\\")
                                     || (e.Category != null && EF.Functions.ILike(e.Category.Name, pattern, "\\"))
                                     || (e.Account != null && EF.Functions.ILike(e.Account.Name, pattern, "\\")));
        }
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 50, 1, 100);
        if (!v.IsValid) return v.Problem();
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.EffectiveDate).ThenByDescending(e => e.CreatedAt)
            .Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return TypedResults.Ok(new PageResult<object>(items.Select(Dto).ToList(), p, size, total));
    }

    public static string LikeEscape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static async Task<IResult> Get(Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var e = await db.Entries.AsNoTracking().Include(x => x.Account).Include(x => x.Category)
            .SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct);
        return e is null ? Problems.NotFound() : TypedResults.Ok(Dto(e));
    }

    private sealed record Validated(EntryKind Kind, string Concept, long Amount, Currency Currency, DateOnly Date, Account? Account, Category? Category, string? Note);

    private static async Task<(Validated? value, Validation v)> ValidateAsync(EntryRequest req, Guid owner, AppDbContext db, CancellationToken ct)
    {
        var v = new Validation();
        var kind = v.Enum("kind", req.Kind, ApiMaps.EntryKinds, "Elegí ingreso o gasto.");
        var concept = v.RequiredText("concept", req.Concept, 90, "El concepto");
        var amount = v.Amount("amountMinor", req.AmountMinor);
        var currency = v.Currency("currency", req.Currency);
        var date = v.Date("date", req.Date);
        var note = v.OptionalText("note", req.Note, 500, "La nota");
        var account = await Refs.AccountAsync(db, owner, req.AccountId, v, ct);
        var category = await Refs.CategoryAsync(db, owner, req.CategoryId, v, ct);
        if (account is not null && v.IsValid && account.Currency != currency) v.Add("accountId", "La cuenta es de otra moneda.");
        if (account is not null && account.Kind == AccountKind.CreditCard && kind == EntryKind.Income)
            v.Add("accountId", "Un ingreso no puede registrarse en una tarjeta de crédito.");
        return v.IsValid ? (new Validated(kind, concept!, amount, currency, date, account, category, note), v) : (null, v);
    }

    private static async Task<IResult> Create(EntryRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var (val, v) = await ValidateAsync(req, owner, db, ct);
        if (val is null) return v.Problem();
        var entry = new Entry
        {
            OwnerId = owner, Kind = val.Kind, Concept = val.Concept, AmountMinor = val.Amount, Currency = val.Currency,
            EffectiveDate = val.Date, AccountId = val.Account?.Id, CategoryId = val.Category?.Id, Note = val.Note,
            Account = val.Account, Category = val.Category,
        };
        db.Entries.Add(entry);
        audit.Add(db, "entry.create", "success", "entry", entry.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/entries/{entry.Id}", Dto(entry));
    }

    private static async Task<IResult> Update(Guid id, EntryRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var (val, v) = await ValidateAsync(req, owner, db, ct);
        if (req.Version is null) { v.Add("version", "Falta la versión."); }
        if (val is null || !v.IsValid) return v.Problem();
        var entry = await db.Entries.Include(e => e.Account).Include(e => e.Category)
            .SingleOrDefaultAsync(e => e.Id == id && e.OwnerId == owner && e.DeletedAt == null, ct);
        if (entry is null) return Problems.NotFound();
        if (entry.Version != req.Version) return Problems.VersionConflict(Dto(entry));
        if (entry.PaidMinor > 0 && (val.Amount < entry.PaidMinor || val.Currency != entry.Currency || val.Account?.Id != entry.AccountId || val.Kind != entry.Kind))
        {
            return Problems.Conflict("Este gasto tiene pagos aplicados. Revertí los pagos antes de cambiar importe, moneda, tipo o tarjeta.");
        }
        entry.Kind = val.Kind;
        entry.Concept = val.Concept;
        entry.AmountMinor = val.Amount;
        entry.Currency = val.Currency;
        entry.EffectiveDate = val.Date;
        entry.AccountId = val.Account?.Id;
        entry.Account = val.Account;
        entry.CategoryId = val.Category?.Id;
        entry.Category = val.Category;
        entry.Note = val.Note;
        audit.Add(db, "entry.update", "success", "entry", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(Dto(entry)) : Problems.VersionConflict();
    }

    private static async Task<IResult> Delete(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var entry = await db.Entries.SingleOrDefaultAsync(e => e.Id == id && e.OwnerId == owner && e.DeletedAt == null, ct);
        if (entry is null) return Problems.NotFound();
        if (version is null || entry.Version != version) return Problems.VersionConflict(Dto(entry));
        if (entry.PaidMinor > 0) return Problems.Conflict("Este gasto tiene pagos aplicados. Revertí los pagos antes de eliminarlo.");
        entry.DeletedAt = clock.GetUtcNow();
        audit.Add(db, "entry.delete", "success", "entry", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(Dto(entry)) : Problems.VersionConflict();
    }

    private static async Task<IResult> Restore(Guid id, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var entry = await db.Entries.SingleOrDefaultAsync(e => e.Id == id && e.OwnerId == owner && e.DeletedAt != null, ct);
        if (entry is null) return Problems.NotFound();
        entry.DeletedAt = null;
        audit.Add(db, "entry.restore", "success", "entry", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(Dto(entry)) : Problems.VersionConflict();
    }
}

/// <summary>Resolución de referencias SIEMPRE filtrada por propietario de la sesión. Ajeno o inexistente → mismo error.</summary>
public static class Refs
{
    public static async Task<Account?> AccountAsync(AppDbContext db, Guid owner, Guid? id, Validation v, CancellationToken ct, string field = "accountId")
    {
        if (id is null) return null;
        var a = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner && x.DeletedAt == null, ct);
        if (a is null) v.Add(field, "La cuenta no existe.");
        return a;
    }

    public static async Task<Category?> CategoryAsync(AppDbContext db, Guid owner, Guid? id, Validation v, CancellationToken ct)
    {
        if (id is null) return null;
        var c = await db.Categories.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner && x.DeletedAt == null, ct);
        if (c is null) v.Add("categoryId", "La categoría no existe.");
        return c;
    }
}
