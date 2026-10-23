using System.Globalization;
using AlvarezFinanzas.Domain;

namespace AlvarezFinanzas.Application;

/// <summary>
/// Códigos de texto estables de los enums del dominio y formato de fechas. Son los mismos en la API y en el archivo de
/// exportación v2, por eso viven en Application: la capa Api y el parser de importación dependen de ellos, y no al revés.
/// </summary>
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

/// <summary>Importe con signo en unidades menores ("-1500", "0", "250000"), usado solo para saldos iniciales.</summary>
public static class SignedMinor
{
    public static bool TryParse(string? value, out long minor)
    {
        minor = 0;
        if (string.IsNullOrEmpty(value)) return false;
        var negative = value[0] == '-';
        var digits = negative ? value[1..] : value;
        if (digits == "0" && !negative) return true;
        if (!Money.TryParseMinor(digits, out var abs)) return false;
        minor = negative ? -abs : abs;
        return true;
    }
}
