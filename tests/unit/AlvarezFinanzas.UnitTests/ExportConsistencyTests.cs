using System.Text;
using System.Text.RegularExpressions;
using AlvarezFinanzas.Application;
using Xunit;

namespace AlvarezFinanzas.UnitTests;

/// <summary>Reglas entre registros de una exportación v2 (moneda, tarjetas, transferencias, pagos).</summary>
public class ExportConsistencyTests
{
    private static string File(string accounts, string entries = "[]", string plans = "[]", string payments = "[]", string transfers = "[]") =>
        $$"""{"format":"alvarez-finanzas-export","version":2,"exportedAt":"2026-10-20T00:00:00Z","accounts":{{accounts}},"categories":[],"entries":{{entries}},"plans":{{plans}},"payments":{{payments}},"transfers":{{transfers}}}""";

    private const string Accounts = """
        [{"id":"ars","name":"Banco","kind":"bank","currency":"ARS"},{"id":"usd","name":"Caja","kind":"cash","currency":"USD"},
         {"id":"visa","name":"Visa","kind":"credit_card","currency":"ARS"},{"id":"mp","name":"MP","kind":"wallet","currency":"ARS"}]
        """;

    private static ParseOutcome Parse(string json) => ImportParser.Parse(Encoding.UTF8.GetBytes(json), 10_000);

    [Fact]
    public void Coherent_file_has_no_errors()
    {
        var o = Parse(File(Accounts,
            entries: """[{"id":"e1","kind":"expense","concept":"Cena","amountMinor":"500","currency":"ARS","date":"2026-10-06","accountId":"visa"}]""",
            payments: """[{"id":"p1","sourceAccountId":"ars","date":"2026-10-07","currency":"ARS","amountMinor":"500","allocations":[{"entryId":"e1","amountMinor":"500"}]}]""",
            transfers: """[{"id":"t1","fromAccountId":"ars","toAccountId":"mp","currency":"ARS","amountMinor":"100","date":"2026-10-07"}]"""));
        Assert.Empty(o.Errors);
        Assert.Equal(7, o.Records.Count);
    }

    [Theory]
    [InlineData("""[{"id":"t1","fromAccountId":"ars","toAccountId":"usd","currency":"ARS","amountMinor":"100","date":"2026-10-07"}]""", "monedas distintas")]
    [InlineData("""[{"id":"t1","fromAccountId":"usd","toAccountId":"usd","currency":"USD","amountMinor":"100","date":"2026-10-07"}]""", "distintos")]
    [InlineData("""[{"id":"t1","fromAccountId":"ars","toAccountId":"visa","currency":"ARS","amountMinor":"100","date":"2026-10-07"}]""", "tarjeta de crédito")]
    [InlineData("""[{"id":"t1","fromAccountId":"ars","toAccountId":"nada","currency":"ARS","amountMinor":"100","date":"2026-10-07"}]""", "no está en el archivo")]
    public void Transfers_follow_the_same_rules_as_the_api(string transfers, string fragment)
    {
        var o = Parse(File(Accounts, transfers: transfers));
        Assert.Contains(o.Errors, e => e.StartsWith("transfers 1:") && e.Contains(fragment));
    }

    [Theory]
    [InlineData("""[{"id":"e1","kind":"expense","concept":"x","amountMinor":"500","currency":"USD","date":"2026-10-06","accountId":"ars"}]""", "otra moneda")]
    [InlineData("""[{"id":"e1","kind":"income","concept":"x","amountMinor":"500","currency":"ARS","date":"2026-10-06","accountId":"visa"}]""", "ingreso")]
    [InlineData("""[{"id":"e1","kind":"income","concept":"x","amountMinor":"500","currency":"ARS","date":"2026-10-06","categoryId":"c9"}]""", "categoría")]
    public void Entries_follow_the_same_rules_as_the_api(string entries, string fragment)
    {
        Assert.Contains(Parse(File(Accounts, entries: entries)).Errors, e => e.StartsWith("entries 1:") && e.Contains(fragment));
    }

    private static string Plan(string extra, string kind = "card", string currency = "ARS", string account = "visa") =>
        $$"""
        [{"id":"pl","kind":"{{kind}}","concept":"Monitor","totalMinor":"300","count":3,"currency":"{{currency}}","firstDueDate":"2026-10-10","accountId":"{{account}}","paidBeforeCount":0{{extra}},
          "installments":[{"number":1,"amountMinor":"100"},{"number":2,"amountMinor":"100"},{"number":3,"amountMinor":"100"}]}]
        """;

    [Theory]
    [InlineData("", "card", "USD", "visa", "otra moneda")]
    [InlineData(""","disbursementMinor":"1000","disbursementDate":"2026-10-01" """, "card", "ARS", "visa", "solo aplica a préstamos")]
    [InlineData(""","disbursementMinor":"1000" """, "loan", "ARS", "ars", "fecha de acreditación")]
    [InlineData(""","disbursementMinor":"1000","disbursementDate":"2026-10-01","disbursementAccountId":"visa" """, "loan", "ARS", "ars", "tarjeta")]
    [InlineData(""","disbursementMinor":"1000","disbursementDate":"2026-10-01","disbursementAccountId":"usd" """, "loan", "ARS", "ars", "otra moneda")]
    [InlineData(""","disbursementAccountId":"ars" """, "loan", "ARS", "ars", "requieren el capital")]
    public void Plans_follow_the_same_rules_as_the_api(string extra, string kind, string currency, string account, string fragment)
    {
        Assert.Contains(Parse(File(Accounts, plans: Plan(extra, kind, currency, account))).Errors, e => e.StartsWith("plans 1:") && e.Contains(fragment));
    }

    [Theory]
    [InlineData("ars", "USD", """[{"planId":"pl","installmentNumber":1,"amountMinor":"100"}]""", "100", "otra moneda")]
    [InlineData("visa", "ARS", """[{"planId":"pl","installmentNumber":1,"amountMinor":"100"}]""", "100", "caja, banco o billetera")]
    [InlineData("ars", "ARS", """[{"planId":"pl","installmentNumber":1,"amountMinor":"101"}]""", "101", "superan lo pendiente")]
    [InlineData("ars", "ARS", """[{"planId":"pl","installmentNumber":1,"amountMinor":"50"},{"planId":"pl","installmentNumber":1,"amountMinor":"50"}]""", "100", "repetidas")]
    [InlineData("ars", "ARS", """[{"planId":"pl","installmentNumber":1,"amountMinor":"100"}]""", "99", "no coincide con la suma")]
    [InlineData("ars", "ARS", """[{"planId":"pl","installmentNumber":4,"amountMinor":"100"}]""", "100", "no existe en su plan")]
    [InlineData("ars", "ARS", """[{"entryId":"e1","amountMinor":"100"}]""", "100", "un gasto pagado no está")]
    public void Payments_follow_the_same_rules_as_the_api(string source, string currency, string allocations, string amount, string fragment)
    {
        var payments = $$"""[{"id":"p1","sourceAccountId":"{{source}}","date":"2026-10-07","currency":"{{currency}}","amountMinor":"{{amount}}","allocations":{{allocations}}}]""";
        Assert.Contains(Parse(File(Accounts, plans: Plan(""), payments: payments)).Errors, e => e.StartsWith("payments 1:") && e.Contains(fragment));
    }

    [Fact]
    public void Payments_accumulate_across_the_file_and_cannot_pay_declared_installments()
    {
        var twice = """
            [{"id":"p1","sourceAccountId":"ars","date":"2026-10-07","currency":"ARS","amountMinor":"60","allocations":[{"planId":"pl","installmentNumber":2,"amountMinor":"60"}]},
             {"id":"p2","sourceAccountId":"ars","date":"2026-10-08","currency":"ARS","amountMinor":"60","allocations":[{"planId":"pl","installmentNumber":2,"amountMinor":"60"}]}]
            """;
        Assert.Contains(Parse(File(Accounts, plans: Plan(""), payments: twice)).Errors, e => e.StartsWith("payments 2:") && e.Contains("superan lo pendiente"));
        var declared = Plan("").Replace("\"paidBeforeCount\":0", "\"paidBeforeCount\":1");
        var pay = """[{"id":"p1","sourceAccountId":"ars","date":"2026-10-07","currency":"ARS","amountMinor":"10","allocations":[{"planId":"pl","installmentNumber":1,"amountMinor":"10"}]}]""";
        Assert.Contains(Parse(File(Accounts, plans: declared, payments: pay)).Errors, e => e.Contains("declarada pagada"));
    }

    [Fact]
    public void Accounts_cards_have_no_opening_balance_and_ids_cannot_carry_separators()
    {
        var card = """[{"id":"visa","name":"Visa","kind":"credit_card","currency":"ARS","openingBalanceMinor":"100","openingDate":"2026-10-01"}]""";
        Assert.Contains(Parse(File(card)).Errors, e => e.Contains("saldo inicial"));
        var dateOnly = """[{"id":"ars","name":"Banco","kind":"bank","currency":"ARS","openingDate":"2026-10-01"}]""";
        Assert.Contains(Parse(File(dateOnly)).Errors, e => e.Contains("van juntos"));
        var twins = """[{"id":"a","name":"Banco","kind":"bank","currency":"ARS"},{"id":"b","name":"Banco","kind":"cash","currency":"ARS"}]""";
        Assert.Contains(Parse(File(twins)).Errors, e => e.Contains("mismo nombre"));
        var archivedTwin = """[{"id":"a","name":"Banco","kind":"bank","currency":"ARS"},{"id":"b","name":"Banco","kind":"cash","currency":"ARS","deleted":true}]""";
        Assert.Empty(Parse(File(archivedTwin)).Errors);
        Assert.Contains(Parse(File("""[{"id":"a:b","name":"Banco","kind":"bank","currency":"ARS"}]""")).Errors, e => e.Contains("identificador"));
    }

    [Fact]
    public void Legacy_preview_always_warns_about_missing_balances_payments_and_due_dates()
    {
        var o = Parse("""{"version":1,"records":[{"id":"a","type":"income","name":"x","amount":5,"currency":"ARS","date":"2026-01-01","category":"","account":"","count":1}]}""");
        Assert.Empty(o.Errors);
        Assert.Contains(ImportParser.LegacyLimitsWarning, o.Warnings);
        Assert.DoesNotContain(o.Warnings, w => w.Contains("pendientes"));
    }
}

/// <summary>Dependencias entre capas del monolito: Domain no conoce a nadie; Application no conoce la capa Api.</summary>
public class LayeringTests
{
    private static string ApiProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "api", "AlvarezFinanzas.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("No se encontró src/api/AlvarezFinanzas.Api desde " + AppContext.BaseDirectory);
    }

    private static IEnumerable<(string file, string line)> Lines(string layer) =>
        Directory.EnumerateFiles(Path.Combine(ApiProject(), layer), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => System.IO.File.ReadLines(f).Select(l => (Path.GetFileName(f), l)));

    [Theory]
    [InlineData("Application", @"\bAlvarezFinanzas\.Api\b|\bApi\.[A-Z]")]
    [InlineData("Domain", @"\bAlvarezFinanzas\.(Api|Application|Infrastructure)\b|\b(Api|Application|Infrastructure)\.[A-Z]")]
    public void Layer_does_not_reference_outer_layers(string layer, string forbidden)
    {
        var offending = Lines(layer).Where(x => Regex.IsMatch(x.line, forbidden)).Select(x => $"{x.file}: {x.line.Trim()}").ToList();
        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }
}
