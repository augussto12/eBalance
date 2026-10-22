namespace AlvarezFinanzas.Domain;

/// <summary>
/// Fechas financieras: fechas locales sin conversión UTC.
/// </summary>
public static class FinanceCalendar
{
    public static readonly TimeZoneInfo ArgentinaZone = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    /// <summary>
    /// Vencimiento de la cuota número <paramref name="number"/> (1-based), anclado al día ORIGINAL de la primera
    /// cuota y ajustado al último día válido del mes destino. 31/01 → 28 o 29/02 → 31/03.
    /// No encadena AddMonths sobre fechas ajustadas.
    /// </summary>
    public static DateOnly DueDate(DateOnly first, int number)
    {
        if (number < 1) throw new ArgumentOutOfRangeException(nameof(number));
        var monthIndex = first.Year * 12 + (first.Month - 1) + (number - 1);
        var year = monthIndex / 12;
        var month = monthIndex % 12 + 1;
        if (year > 9999) throw new ArgumentOutOfRangeException(nameof(number));
        var day = Math.Min(first.Day, DateTime.DaysInMonth(year, month));
        return new DateOnly(year, month, day);
    }

    public static int MonthIndex(DateOnly date) => date.Year * 12 + date.Month - 1;

    public static DateOnly FirstOfMonthIndex(int index) => new(index / 12, index % 12 + 1, 1);

    public static bool TryParseMonth(string? value, out DateOnly firstDay)
    {
        firstDay = default;
        if (value is null || value.Length != 7 || value[4] != '-') return false;
        if (!int.TryParse(value.AsSpan(0, 4), System.Globalization.NumberStyles.None, null, out var y)) return false;
        if (!int.TryParse(value.AsSpan(5, 2), System.Globalization.NumberStyles.None, null, out var m)) return false;
        if (y is < 1900 or > 2200 || m is < 1 or > 12) return false;
        firstDay = new DateOnly(y, m, 1);
        return true;
    }

    public static string FormatMonth(DateOnly d) => $"{d.Year:D4}-{d.Month:D2}";

    public static DateOnly Today(TimeProvider clock, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);

    public static readonly DateOnly MinDate = new(1900, 1, 1);
    public static readonly DateOnly MaxDate = new(2200, 12, 31);

    public static bool IsInRange(DateOnly d) => d >= MinDate && d <= MaxDate;
}
