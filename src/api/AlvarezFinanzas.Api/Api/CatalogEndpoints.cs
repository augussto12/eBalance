using System.Globalization;
using AlvarezFinanzas.Application;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;

namespace AlvarezFinanzas.Api;

/// <summary>Cuentas y categorías del propietario.</summary>
public static class CatalogEndpoints
{
    public sealed record AccountRequest(string? Name, string? Kind, string? Currency, string? Last4,
        string? OpeningBalanceMinor, string? OpeningDate, long? Version);
    public sealed record CategoryRequest(string? Name, string? Kind, long? Version);

    public static void Map(RouteGroupBuilder api)
    {
        var a = api.MapGroup("/accounts");
        a.MapGet("/", ListAccounts);
        a.MapGet("/{id:guid}", GetAccount);
        a.MapPost("/", CreateAccount);
        a.MapPatch("/{id:guid}", UpdateAccount);
        a.MapDelete("/{id:guid}", DeleteAccount);
        a.MapPost("/{id:guid}/restore", RestoreAccount);

        var c = api.MapGroup("/categories");
        c.MapGet("/", ListCategories);
        c.MapGet("/{id:guid}", GetCategory);
        c.MapPost("/", CreateCategory);
        c.MapPatch("/{id:guid}", UpdateCategory);
        c.MapDelete("/{id:guid}", DeleteCategory);
        c.MapPost("/{id:guid}/restore", RestoreCategory);
    }

    private const string CardOpeningMessage = "Las tarjetas no llevan saldo inicial; sus obligaciones se calculan por cuotas y gastos.";

    public static object AccountDto(Account a, AccountBalance? balance = null) => new
    {
        id = a.Id,
        name = a.Name,
        kind = a.Kind.Api(),
        currency = a.Currency.ToString(),
        last4 = a.Last4,
        openingBalanceMinor = a.OpeningBalanceMinor?.ToString(CultureInfo.InvariantCulture),
        openingDate = a.OpeningDate.Api(),
        version = a.Version,
        deletedAt = a.DeletedAt,
        cashBalanceMinor = balance?.CashBalanceMinor?.ToString(CultureInfo.InvariantCulture),
        cashBalanceAsOf = balance?.AsOf.Api(),
        pendingObligationsMinor = balance?.PendingObligationsMinor?.ToString(CultureInfo.InvariantCulture),
    };

    public static object CategoryDto(Category c) => new
    {
        id = c.Id, name = c.Name, kind = c.Kind.Api(), version = c.Version, deletedAt = c.DeletedAt,
    };

    private static async Task<IResult> ListAccounts(HttpContext ctx, AppDbContext db, BalanceService balances, bool? includeDeleted, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var q = db.Accounts.AsNoTracking().Where(a => a.OwnerId == owner);
        if (includeDeleted != true) q = q.Where(a => a.DeletedAt == null);
        var list = await q.OrderBy(a => a.Name).ThenBy(a => a.Currency).ToListAsync(ct);
        var computed = await balances.ComputeAsync(owner, list, ct);
        return TypedResults.Ok(list.Select(a => AccountDto(a, computed.GetValueOrDefault(a.Id))));
    }

    /// <summary>Una cuenta por ID (también archivada), con su saldo calculado. Ajena o inexistente → 404.</summary>
    private static async Task<IResult> GetAccount(Guid id, HttpContext ctx, AppDbContext db, BalanceService balances, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == owner, ct);
        if (account is null) return Problems.NotFound();
        var computed = await balances.ComputeAsync(owner, [account], ct);
        return TypedResults.Ok(AccountDto(account, computed.GetValueOrDefault(account.Id)));
    }

    private static (string? name, AccountKind kind, Currency currency, string? last4, long? opening, DateOnly? openingDate) ValidateAccount(
        AccountRequest req, Validation v, bool creating)
    {
        var name = v.RequiredText("name", req.Name, 60, "El nombre");
        var kind = creating ? v.Enum("kind", req.Kind, ApiMaps.AccountKinds, "Tipo de cuenta no válido.") : default;
        var currency = creating ? v.Currency("currency", req.Currency) : default;
        string? last4 = null;
        if (!string.IsNullOrWhiteSpace(req.Last4))
        {
            last4 = req.Last4.Trim();
            if (last4.Length != 4 || !last4.All(char.IsAsciiDigit)) v.Add("last4", "Solo los últimos cuatro dígitos.");
        }
        long? opening = null;
        DateOnly? openingDate = null;
        if (!string.IsNullOrEmpty(req.OpeningBalanceMinor) || !string.IsNullOrEmpty(req.OpeningDate))
        {
            if (!SignedMinor.TryParse(req.OpeningBalanceMinor, out var o)) v.Add("openingBalanceMinor", "Saldo inicial no válido.");
            else opening = o;
            openingDate = v.Date("openingDate", req.OpeningDate, "La fecha del saldo inicial");
        }
        return (name, kind, currency, last4, opening, openingDate);
    }

    private static async Task<IResult> CreateAccount(AccountRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var v = new Validation();
        var (name, kind, currency, last4, opening, openingDate) = ValidateAccount(req, v, creating: true);
        if (opening is not null && kind == AccountKind.CreditCard) v.Add("openingBalanceMinor", CardOpeningMessage);
        if (!v.IsValid) return v.Problem();
        var owner = ctx.User.UserId();
        var account = new Account { OwnerId = owner, Name = name!, Kind = kind, Currency = currency, Last4 = last4, OpeningBalanceMinor = opening, OpeningDate = openingDate };
        db.Accounts.Add(account);
        audit.Add(db, "account.create", "success", "account", account.Id);
        if (!await Db.TrySaveAsync(db, ct)) return Problems.Conflict("Ya existe una cuenta con ese nombre y moneda.");
        return TypedResults.Created($"/api/v1/accounts/{account.Id}", AccountDto(account));
    }

    private static async Task<IResult> UpdateAccount(Guid id, AccountRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var v = new Validation();
        var (name, _, _, last4, opening, openingDate) = ValidateAccount(req, v, creating: false);
        if (req.Version is null) v.Add("version", "Falta la versión.");
        if (!v.IsValid) return v.Problem();
        var owner = ctx.User.UserId();
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == owner && a.DeletedAt == null, ct);
        if (account is null) return Problems.NotFound();
        if (account.Version != req.Version) return Problems.VersionConflict(AccountDto(account));
        if (opening is not null && account.Kind == AccountKind.CreditCard)
        {
            // Misma regla y mismo código (400 por campo) que al crear la cuenta.
            v.Add("openingBalanceMinor", CardOpeningMessage);
            return v.Problem();
        }
        account.Name = name!;
        account.Last4 = last4;
        account.OpeningBalanceMinor = opening;
        account.OpeningDate = openingDate;
        audit.Add(db, "account.update", "success", "account", id);
        return await Db.SaveVersionedAsync(db, ct) switch
        {
            SaveOutcome.Ok => TypedResults.Ok(AccountDto(account)),
            SaveOutcome.Duplicate => Problems.Conflict("Ya existe una cuenta con ese nombre y moneda."),
            _ => Problems.VersionConflict(),
        };
    }

    private static async Task<IResult> DeleteAccount(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == owner && a.DeletedAt == null, ct);
        if (account is null) return Problems.NotFound();
        if (version is null || account.Version != version) return Problems.VersionConflict(AccountDto(account));
        // Archivar: los registros históricos conservan la referencia.
        account.DeletedAt = clock.GetUtcNow();
        audit.Add(db, "account.delete", "success", "account", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(AccountDto(account)) : Problems.VersionConflict();
    }

    /// <summary>Restaura una cuenta archivada. Igual que el archivado, exige la versión actual (?version=).</summary>
    private static async Task<IResult> RestoreAccount(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == owner && a.DeletedAt != null, ct);
        if (account is null) return Problems.NotFound();
        if (version is null || account.Version != version) return Problems.VersionConflict(AccountDto(account));
        account.DeletedAt = null;
        audit.Add(db, "account.restore", "success", "account", id);
        return await Db.SaveVersionedAsync(db, ct) switch
        {
            SaveOutcome.Ok => TypedResults.Ok(AccountDto(account)),
            SaveOutcome.Duplicate => Problems.Conflict("Ya existe otra cuenta activa con ese nombre y moneda."),
            _ => Problems.VersionConflict(),
        };
    }

    private static async Task<IResult> ListCategories(HttpContext ctx, AppDbContext db, bool? includeDeleted, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var q = db.Categories.AsNoTracking().Where(c => c.OwnerId == owner);
        if (includeDeleted != true) q = q.Where(c => c.DeletedAt == null);
        var list = await q.OrderBy(c => c.Name).ToListAsync(ct);
        return TypedResults.Ok(list.Select(CategoryDto));
    }

    /// <summary>Una categoría por ID (también archivada). Ajena o inexistente → 404.</summary>
    private static async Task<IResult> GetCategory(Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.OwnerId == owner, ct);
        return category is null ? Problems.NotFound() : TypedResults.Ok(CategoryDto(category));
    }

    private static async Task<IResult> CreateCategory(CategoryRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var v = new Validation();
        var name = v.RequiredText("name", req.Name, 40, "El nombre");
        var kind = req.Kind is null ? CategoryKind.Any : v.Enum("kind", req.Kind, ApiMaps.CategoryKinds, "Tipo de categoría no válido.");
        if (!v.IsValid) return v.Problem();
        var category = new Category { OwnerId = ctx.User.UserId(), Name = name!, Kind = kind };
        db.Categories.Add(category);
        audit.Add(db, "category.create", "success", "category", category.Id);
        if (!await Db.TrySaveAsync(db, ct)) return Problems.Conflict("Ya existe una categoría con ese nombre.");
        return TypedResults.Created($"/api/v1/categories/{category.Id}", CategoryDto(category));
    }

    private static async Task<IResult> UpdateCategory(Guid id, CategoryRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var v = new Validation();
        var name = v.RequiredText("name", req.Name, 40, "El nombre");
        var kind = req.Kind is null ? (CategoryKind?)null : v.Enum("kind", req.Kind, ApiMaps.CategoryKinds, "Tipo de categoría no válido.");
        if (req.Version is null) v.Add("version", "Falta la versión.");
        if (!v.IsValid) return v.Problem();
        var owner = ctx.User.UserId();
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id && c.OwnerId == owner && c.DeletedAt == null, ct);
        if (category is null) return Problems.NotFound();
        if (category.Version != req.Version) return Problems.VersionConflict(CategoryDto(category));
        category.Name = name!;
        if (kind is not null) category.Kind = kind.Value;
        audit.Add(db, "category.update", "success", "category", id);
        return await Db.SaveVersionedAsync(db, ct) switch
        {
            SaveOutcome.Ok => TypedResults.Ok(CategoryDto(category)),
            SaveOutcome.Duplicate => Problems.Conflict("Ya existe una categoría con ese nombre."),
            _ => Problems.VersionConflict(),
        };
    }

    private static async Task<IResult> DeleteCategory(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id && c.OwnerId == owner && c.DeletedAt == null, ct);
        if (category is null) return Problems.NotFound();
        if (version is null || category.Version != version) return Problems.VersionConflict(CategoryDto(category));
        category.DeletedAt = clock.GetUtcNow();
        audit.Add(db, "category.delete", "success", "category", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(CategoryDto(category)) : Problems.VersionConflict();
    }

    /// <summary>Restaura una categoría archivada. Igual que el archivado, exige la versión actual (?version=).</summary>
    private static async Task<IResult> RestoreCategory(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id && c.OwnerId == owner && c.DeletedAt != null, ct);
        if (category is null) return Problems.NotFound();
        if (version is null || category.Version != version) return Problems.VersionConflict(CategoryDto(category));
        category.DeletedAt = null;
        audit.Add(db, "category.restore", "success", "category", id);
        return await Db.SaveVersionedAsync(db, ct) switch
        {
            SaveOutcome.Ok => TypedResults.Ok(CategoryDto(category)),
            SaveOutcome.Duplicate => Problems.Conflict("Ya existe otra categoría activa con ese nombre."),
            _ => Problems.VersionConflict(),
        };
    }
}

public enum SaveOutcome { Ok, Concurrency, Duplicate }

public static class Db
{
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    public static async Task<bool> TrySaveAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public static async Task<SaveOutcome> SaveVersionedAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return SaveOutcome.Ok;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return SaveOutcome.Concurrency;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return SaveOutcome.Duplicate;
        }
    }
}
