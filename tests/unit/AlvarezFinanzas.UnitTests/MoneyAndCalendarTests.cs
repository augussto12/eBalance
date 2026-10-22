using System.Text;
using AlvarezFinanzas.Application;
using AlvarezFinanzas.Domain;
using Xunit;

namespace AlvarezFinanzas.UnitTests;

public class MoneyTests
{
    [Fact]
    public void Distribute_10000_in_3_gives_3334_3333_3333()
    {
        Assert.Equal([3334L, 3333L, 3333L], Money.Distribute(10_000, 3));
    }

    [Theory]
    [InlineData(48_000_000L, 6)]
    [InlineData(72_000_000L, 6)]
    [InlineData(100L, 7)]
    [InlineData(99_999_999_999_999L, 360)]
    public void Distribute_sums_exactly_and_differs_by_at_most_one(long total, int count)
    {
        var parts = Money.Distribute(total, count);
        Assert.Equal(count, parts.Length);
        Assert.Equal(total, parts.Sum());
        Assert.True(parts.Max() - parts.Min() <= 1);
        Assert.True(parts.SequenceEqual(parts.OrderByDescending(p => p)), "las cuotas con +1 van primero");
    }

    [Fact]
    public void Distribute_rejects_more_installments_than_cents()
    {
        Assert.Throws<ArgumentException>(() => Money.Distribute(1, 3));
    }

    [Theory]
    [InlineData("480000", 48_000_000L)]
    [InlineData("1234.5", 123_450L)]
    [InlineData("1234.56", 123_456L)]
    [InlineData("0.01", 1L)]
    [InlineData("10.10", 1_010L)]
    [InlineData("1000000000000", 100_000_000_000_000L)]
    public void ParseDecimalText_is_exact(string text, long expected)
    {
        var r = Money.ParseDecimalText(text);
        Assert.Equal(DecimalParseStatus.Ok, r.Status);
        Assert.Equal(expected, r.Minor);
    }

    [Theory]
    [InlineData("1.234", DecimalParseStatus.TooManyDecimals)]
    [InlineData("0", DecimalParseStatus.NotPositive)]
    [InlineData("-5", DecimalParseStatus.Invalid)]
    [InlineData("1e5", DecimalParseStatus.Invalid)]
    [InlineData("NaN", DecimalParseStatus.Invalid)]
    [InlineData("1000000000000.01", DecimalParseStatus.OutOfRange)]
    [InlineData("99999999999999999999", DecimalParseStatus.OutOfRange)]
    public void ParseDecimalText_rejects_without_truncating(string text, DecimalParseStatus expected)
    {
        Assert.Equal(expected, Money.ParseDecimalText(text).Status);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("100000000000000", true)]
    [InlineData("100000000000001", false)]
    [InlineData("0", false)]
    [InlineData("01", false)]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    [InlineData(" 1", false)]
    [InlineData("", false)]
    public void TryParseMinor_accepts_only_positive_integer_strings(string text, bool ok)
    {
        Assert.Equal(ok, Money.TryParseMinor(text, out _));
    }
}

public class CalendarTests
{
    [Fact]
    public void Anchors_to_original_day_and_clamps_to_month_end()
    {
        var first = new DateOnly(2028, 1, 31);
        Assert.Equal(new DateOnly(2028, 1, 31), FinanceCalendar.DueDate(first, 1));
        Assert.Equal(new DateOnly(2028, 2, 29), FinanceCalendar.DueDate(first, 2)); // bisiesto
        Assert.Equal(new DateOnly(2028, 3, 31), FinanceCalendar.DueDate(first, 3)); // no encadena el 29
        Assert.Equal(new DateOnly(2028, 4, 30), FinanceCalendar.DueDate(first, 4));
    }

    [Fact]
    public void Non_leap_february_clamps_to_28()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), FinanceCalendar.DueDate(new DateOnly(2027, 1, 31), 2));
    }

    [Fact]
    public void Crosses_december_to_january()
    {
        var first = new DateOnly(2026, 11, 15);
        Assert.Equal(new DateOnly(2026, 12, 15), FinanceCalendar.DueDate(first, 2));
        Assert.Equal(new DateOnly(2027, 1, 15), FinanceCalendar.DueDate(first, 3));
        Assert.Equal(new DateOnly(2027, 2, 15), FinanceCalendar.DueDate(first, 4));
    }

    [Fact]
    public void Today_uses_argentina_zone_without_shifting_dates()
    {
        // 02:30 UTC del 1/1 es todavía 31/12 en Buenos Aires (UTC−3).
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 1, 2, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 12, 31), FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone));
    }

    [Fact]
    public void Plan_schedule_builds_exact_installments()
    {
        var plan = new InstallmentPlan { Concept = "x", TotalMinor = 10_000, Count = 3, FirstDueDate = new DateOnly(2028, 1, 31), PaidBeforeCount = 1 };
        var list = PlanSchedule.Build(plan);
        Assert.Equal([3334L, 3333L, 3333L], list.Select(i => i.AmountMinor));
        Assert.Equal([new DateOnly(2028, 1, 31), new DateOnly(2028, 2, 29), new DateOnly(2028, 3, 31)], list.Select(i => i.DueDate));
        Assert.True(list[0].DeclaredPaid);
        Assert.False(list[1].DeclaredPaid);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public class ImportParserTests
{
    private static ParseOutcome Parse(string json, int max = 10_000) => ImportParser.Parse(Encoding.UTF8.GetBytes(json), max);

    [Fact]
    public void Parses_prototype_backup_with_amount_as_total()
    {
        var o = Parse("""
            {"version":1,"records":[
              {"id":"a1","type":"card","name":"Monitor","amount":480000,"currency":"ARS","date":"2026-10-05","category":"Tecnología","account":"Visa","count":6},
              {"id":"lt2abc","type":"expense","name":"Café","amount":12500.5,"currency":"USD","date":"2026-10-05","category":"Ocio","account":"","count":1}
            ]}
            """);
        Assert.Empty(o.Errors);
        Assert.Equal(2, o.Records.Count);
        Assert.Equal("48000000", o.Records[0].Fields["amountMinor"]);
        Assert.Equal("6", o.Records[0].Fields["count"]);
        Assert.Equal("plan", o.Records[0].Target);
        Assert.Equal("1250050", o.Records[1].Fields["amountMinor"]);
        Assert.Null(o.Records[1].Fields["account"]);
        Assert.Contains(o.Warnings, w => w.Contains("pendientes"));
    }

    [Theory]
    [InlineData("""{"version":1,"records":[{"id":"a","type":"expense","name":"x","amount":1.234,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":1}]}""", "más de dos decimales")]
    [InlineData("""{"version":1,"records":[{"id":"a","type":"expense","name":"x","amount":-5,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":1}]}""", "importe")]
    [InlineData("""{"version":1,"records":[{"id":"a","type":"expense","name":"x","amount":5,"currency":"EUR","date":"2026-01-01","category":"","account":"","count":1}]}""", "moneda")]
    [InlineData("""{"version":1,"records":[{"id":"a","type":"expense","name":"x","amount":5,"currency":"ARS","date":"2026-02-30","category":"","account":"","count":1}]}""", "fecha")]
    [InlineData("""{"version":1,"records":[{"id":"a","type":"hack","name":"x","amount":5,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":1}]}""", "tipo")]
    [InlineData("""{"version":1,"records":[{"id":"a","type":"expense","name":"x","amount":5,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":1,"owner_id":"z"}]}""", "no permitido")]
    [InlineData("""{"version":1,"records":[{"id":"a","type":"card","name":"x","amount":0.01,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":3}]}""", "centavos")]
    [InlineData("""{"version":2,"records":[]}""", "Versión")]
    [InlineData("""not json""", "JSON")]
    public void Rejects_invalid_files(string json, string expectedFragment)
    {
        var o = Parse(json);
        Assert.Contains(o.Errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_duplicate_ids_and_excess_depth_and_record_limit()
    {
        Assert.Contains(Parse("""{"version":1,"records":[{"id":"a","type":"income","name":"x","amount":5,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":1},{"id":"a","type":"income","name":"y","amount":5,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":1}]}""").Errors,
            e => e.Contains("repetido"));
        Assert.NotEmpty(Parse("""{"version":1,"records":[[[[[[[[[[1]]]]]]]]]]}""").Errors);
        Assert.Contains(Parse("""{"version":1,"records":[{},{}]}""", max: 1).Errors, e => e.Contains("máximo"));
    }

    [Fact]
    public void Same_content_produces_same_hash_and_changes_produce_different_hash()
    {
        const string a = """{"version":1,"records":[{"id":"a","type":"income","name":"x","amount":5,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":1}]}""";
        const string b = """{"version":1,"records":[{"id":"a","type":"income","name":"x","amount":6,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":1}]}""";
        Assert.Equal(Parse(a).Records[0].Hash, Parse(a).Records[0].Hash);
        Assert.NotEqual(Parse(a).Records[0].Hash, Parse(b).Records[0].Hash);
    }
}
