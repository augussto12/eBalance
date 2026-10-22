using System.Globalization;
using AlvarezFinanzas.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlvarezFinanzas.Api;

/// <summary>Acumula errores por campo; el servidor es la autoridad de validación.</summary>
public sealed class Validation
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
    }

    public IResult Problem(string title = "Revisá los datos ingresados.") =>
        TypedResults.ValidationProblem(_errors.ToDictionary(k => k.Key, v => v.Value.ToArray()), title: title,
            extensions: null);

    public string? RequiredText(string field, string? value, int max, string label)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) { Add(field, $"{label} es obligatorio."); return null; }
        if (v.Length > max) { Add(field, $"{label} admite hasta {max} caracteres."); return null; }
        if (v.Any(char.IsControl)) { Add(field, $"{label} contiene caracteres no permitidos."); return null; }
        return v;
    }

    public string? OptionalText(string field, string? value, int max, string label)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length > max) { Add(field, $"{label} admite hasta {max} caracteres."); return null; }
        if (v.Any(c => char.IsControl(c) && c != '\n')) { Add(field, $"{label} contiene caracteres no permitidos."); return null; }
        return v;
    }

    public long Amount(string field, string? value, string label = "El importe")
    {
        if (Money.TryParseMinor(value, out var minor)) return minor;
        Add(field, $"{label} debe ser un número positivo de hasta 1.000.000.000.000,00.");
        return 0;
    }

    public Currency Currency(string field, string? value)
    {
        if (Money.TryParseCurrency(value, out var c)) return c;
        Add(field, "La moneda debe ser ARS o USD.");
        return default;
    }

    public DateOnly Date(string field, string? value, string label = "La fecha")
    {
        if (value is { Length: 10 }
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            && FinanceCalendar.IsInRange(d))
        {
            return d;
        }
        Add(field, $"{label} no es válida.");
        return default;
    }

    public DateOnly? OptionalDate(string field, string? value, string label = "La fecha") =>
        string.IsNullOrEmpty(value) ? null : Date(field, value, label);

    public T Enum<T>(string field, string? value, IReadOnlyDictionary<string, T> map, string message) where T : struct
    {
        if (value is not null && map.TryGetValue(value, out var r)) return r;
        Add(field, message);
        return default;
    }
}

public static class ApiMaps
{
    public static readonly IReadOnlyDictionary<string, EntryKind> EntryKinds = new Dictionary<string, EntryKind>
    {
        ["income"] = EntryKind.Income, ["expense"] = EntryKind.Expense,
    };
    public static readonly IReadOnlyDictionary<string, PlanKind> PlanKinds = new Dictionary<string, PlanKind>
    {
        ["card"] = PlanKind.Card, ["loan"] = PlanKind.Loan,
    };
    public static readonly IReadOnlyDictionary<string, AccountKind> AccountKinds = new Dictionary<string, AccountKind>
    {
        ["cash"] = AccountKind.Cash, ["bank"] = AccountKind.Bank, ["wallet"] = AccountKind.Wallet, ["credit_card"] = AccountKind.CreditCard,
    };
    public static readonly IReadOnlyDictionary<string, CategoryKind> CategoryKinds = new Dictionary<string, CategoryKind>
    {
        ["expense"] = CategoryKind.Expense, ["income"] = CategoryKind.Income, ["any"] = CategoryKind.Any,
    };

    public static string Api(this EntryKind k) => k == EntryKind.Income ? "income" : "expense";
    public static string Api(this PlanKind k) => k == PlanKind.Card ? "card" : "loan";
    public static string Api(this AccountKind k) => k switch
    {
        AccountKind.Cash => "cash", AccountKind.Bank => "bank", AccountKind.Wallet => "wallet", _ => "credit_card",
    };
    public static string Api(this CategoryKind k) => k switch
    {
        CategoryKind.Expense => "expense", CategoryKind.Income => "income", _ => "any",
    };
    public static string Api(this DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string? Api(this DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public static class Problems
{
    public static ProblemHttpResult NotFound() =>
        TypedResults.Problem(title: "No encontrado.", statusCode: StatusCodes.Status404NotFound);

    public static ProblemHttpResult Conflict(string title, object? current = null) =>
        TypedResults.Problem(title: title, statusCode: StatusCodes.Status409Conflict,
            extensions: current is null ? null : new Dictionary<string, object?> { ["current"] = current });

    public static ProblemHttpResult VersionConflict(object? current = null) =>
        Conflict("El registro cambió desde que lo abriste. Revisá los datos actuales antes de guardar.", current);

    public static ProblemHttpResult Unprocessable(string title) =>
        TypedResults.Problem(title: title, statusCode: StatusCodes.Status422UnprocessableEntity);

    public static ProblemHttpResult TooMany(TimeSpan retryAfter, HttpContext ctx)
    {
        ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return TypedResults.Problem(title: "Demasiados intentos. Esperá un momento y volvé a probar.", statusCode: StatusCodes.Status429TooManyRequests);
    }

    public static ProblemHttpResult Forbidden(string title) =>
        TypedResults.Problem(title: title, statusCode: StatusCodes.Status403Forbidden);
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
