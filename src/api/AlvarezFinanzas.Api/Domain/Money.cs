using System.Globalization;

namespace AlvarezFinanzas.Domain;

public enum Currency { ARS, USD }

/// <summary>
/// Reglas monetarias exactas. Todo importe es un entero de unidades menores (centavos).
/// Nunca se usa coma flotante como autoridad.
/// </summary>
public static class Money
{
    /// <summary>ARS y USD usan dos decimales.</summary>
    public const int Decimals = 2;

    /// <summary>Límite de negocio: 1.000.000.000.000,00 por importe (1e14 centavos).</summary>
    public const long MaxAmountMinor = 100_000_000_000_000L;

    public const int MaxInstallments = 360;

    public static bool TryParseCurrency(string? value, out Currency currency)
    {
        currency = default;
        return value is "ARS" or "USD" && Enum.TryParse(value, out currency);
    }

    /// <summary>
    /// Parsea un importe transportado como string decimal de unidades menores ("123456" = 1.234,56).
    /// Solo dígitos, positivo, sin signo, sin exponentes.
    /// </summary>
    public static bool TryParseMinor(string? value, out long minor)
    {
        minor = 0;
        if (string.IsNullOrEmpty(value) || value.Length > 18) return false;
        foreach (var c in value)
        {
            if (c is < '0' or > '9') return false;
        }
        if (value.Length > 1 && value[0] == '0') return false;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out minor)) return false;
        return minor is > 0 and <= MaxAmountMinor;
    }

    /// <summary>
    /// Parsea un decimal en texto con punto como separador ("1234.5", "10", "0.01") a unidades menores.
    /// Rechaza más de dos decimales en lugar de truncar.
    /// </summary>
    public static DecimalParseResult ParseDecimalText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 24) return DecimalParseResult.Invalid;
        var parts = text.Split('.');
        if (parts.Length > 2) return DecimalParseResult.Invalid;
        var intPart = parts[0];
        var fracPart = parts.Length == 2 ? parts[1] : "";
        if (intPart.Length == 0 || !intPart.All(char.IsAsciiDigit)) return DecimalParseResult.Invalid;
        if (parts.Length == 2 && (fracPart.Length == 0 || !fracPart.All(char.IsAsciiDigit))) return DecimalParseResult.Invalid;
        if (fracPart.TrimEnd('0').Length > Decimals) return DecimalParseResult.TooManyDecimals;
        fracPart = fracPart.Length > Decimals ? fracPart[..Decimals] : fracPart.PadRight(Decimals, '0');
        intPart = intPart.TrimStart('0');
        if (intPart.Length > 13) return DecimalParseResult.OutOfRange;
        var units = intPart.Length == 0 ? 0 : long.Parse(intPart, CultureInfo.InvariantCulture);
        long minor;
        try
        {
            minor = checked(units * 100 + long.Parse(fracPart, CultureInfo.InvariantCulture));
        }
        catch (OverflowException)
        {
            return DecimalParseResult.OutOfRange;
        }
        if (minor <= 0) return DecimalParseResult.NotPositive;
        if (minor > MaxAmountMinor) return DecimalParseResult.OutOfRange;
        return new DecimalParseResult(DecimalParseStatus.Ok, minor);
    }

    /// <summary>
    /// Distribución exacta: base = total DIV n, resto = total MOD n; las primeras «resto» cuotas llevan base + 1.
    /// 10000 / 3 → 3334, 3333, 3333.
    /// </summary>
    public static long[] Distribute(long totalMinor, int count)
    {
        if (count is < 1 or > MaxInstallments) throw new ArgumentOutOfRangeException(nameof(count));
        if (totalMinor <= 0 || totalMinor > MaxAmountMinor) throw new ArgumentOutOfRangeException(nameof(totalMinor));
        if (count > totalMinor) throw new ArgumentException("Cada cuota debe ser de al menos una unidad menor.", nameof(count));
        var baseAmount = totalMinor / count;
        var remainder = totalMinor % count;
        var result = new long[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = i < remainder ? checked(baseAmount + 1) : baseAmount;
        }
        return result;
    }

    public static long Sum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (var v in values) total = checked(total + v);
        return total;
    }

    public static string ToApi(long minor) => minor.ToString(CultureInfo.InvariantCulture);
}

public enum DecimalParseStatus { Ok, Invalid, TooManyDecimals, NotPositive, OutOfRange }

public readonly record struct DecimalParseResult(DecimalParseStatus Status, long Minor)
{
    public static readonly DecimalParseResult Invalid = new(DecimalParseStatus.Invalid, 0);
    public static readonly DecimalParseResult TooManyDecimals = new(DecimalParseStatus.TooManyDecimals, 0);
    public static readonly DecimalParseResult NotPositive = new(DecimalParseStatus.NotPositive, 0);
    public static readonly DecimalParseResult OutOfRange = new(DecimalParseStatus.OutOfRange, 0);
}
