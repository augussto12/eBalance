using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlvarezFinanzas.Domain;

namespace AlvarezFinanzas.Application;

/// <summary>Registro normalizado listo para importar. Todo texto se trata como texto plano, nunca HTML.</summary>
public sealed record NormalizedRecord(
    string SourceType, string SourceId, string Target, string Hash,
    Dictionary<string, string?> Fields);

public sealed class ParseOutcome
{
    public string Format { get; set; } = "";
    public int FormatVersion { get; set; }
    public List<NormalizedRecord> Records { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Parser estricto de respaldos: JSON UTF-8, profundidad acotada, claves permitidas, límites de texto, enums, fechas y
/// decimales exactos (más de dos decimales → error, nunca truncado). No acepta owner_id del archivo.
/// </summary>
public static class ImportParser
{
    public const string LegacyFormat = "legacy-v1";
    public const string ExportFormat = "alvarez-finanzas-export";
    private const int MaxErrors = 50;

    public static ParseOutcome Parse(ReadOnlyMemory<byte> utf8, int maxRecords)
    {
        var outcome = new ParseOutcome();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            outcome.Errors.Add("El archivo no es JSON válido (UTF-8).");
            return outcome;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { outcome.Errors.Add("El archivo no tiene el formato esperado."); return outcome; }
            if (root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String && fmt.GetString() == ExportFormat)
            {
                ParseExport(root, maxRecords, outcome);
            }
            else
            {
                ParseLegacy(root, maxRecords, outcome);
            }
        }
        return outcome;
    }

    private static void Error(ParseOutcome o, string message)
    {
        if (o.Errors.Count < MaxErrors) o.Errors.Add(message);
    }

    private static bool OnlyKeys(JsonElement obj, ParseOutcome o, string where, params string[] allowed)
    {
        var ok = true;
        foreach (var p in obj.EnumerateObject())
        {
            if (!allowed.Contains(p.Name)) { Error(o, $"{where}: campo no permitido «{Trunc(p.Name)}»."); ok = false; }
        }
        return ok;
    }

    private static string Trunc(string s) => s.Length > 40 ? s[..40] + "…" : s;

    private static string? Str(JsonElement obj, string name, int max, ParseOutcome o, string where, bool required = true, bool allowEmpty = false)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            if (required) Error(o, $"{where}: falta «{name}».");
            return null;
        }
        if (el.ValueKind != JsonValueKind.String) { Error(o, $"{where}: «{name}» debe ser texto."); return null; }
        var s = el.GetString()!.Trim();
        if (s.Length == 0 && !allowEmpty && required) { Error(o, $"{where}: «{name}» está vacío."); return null; }
        if (s.Length > max) { Error(o, $"{where}: «{name}» supera {max} caracteres."); return null; }
        if (s.Any(c => char.IsControl(c) && c != '\n')) { Error(o, $"{where}: «{name}» tiene caracteres no permitidos."); return null; }
        return s;
    }

    private static string? Date(JsonElement obj, string name, ParseOutcome o, string where, bool required = true)
    {
        var s = Str(obj, name, 10, o, where, required);
        if (s is null) return null;
        if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            || !FinanceCalendar.IsInRange(d))
        {
            Error(o, $"{where}: fecha «{Trunc(s)}» no válida.");
            return null;
        }
        return s;
    }

    private static string? Currency(JsonElement obj, ParseOutcome o, string where)
    {
        var s = Str(obj, "currency", 3, o, where);
        if (s is null) return null;
        if (s is not ("ARS" or "USD")) { Error(o, $"{where}: moneda «{Trunc(s)}» no soportada."); return null; }
        return s;
    }

    private static string? MinorString(JsonElement obj, string name, ParseOutcome o, string where, bool required = true, bool allowZero = false)
    {
        var s = Str(obj, name, 18, o, where, required);
        if (s is null) return null;
        if (allowZero && s == "0") return s;
        if (!Money.TryParseMinor(s, out _)) { Error(o, $"{where}: importe «{Trunc(s)}» no válido."); return null; }
        return s;
    }

    private static string Hash(IEnumerable<KeyValuePair<string, string?>> fields)
    {
        var canonical = string.Join("\u001f", fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + "=" + f.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ParseLegacy(JsonElement root, int maxRecords, ParseOutcome o)
    {
        o.Format = LegacyFormat;
        o.FormatVersion = 1;
        OnlyKeys(root, o, "Archivo", "version", "records");
        if (!root.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number || ver.GetRawText() != "1")
        {
            Error(o, "Versión de respaldo no soportada (se espera version: 1).");
            return;
        }
        if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            Error(o, "Falta la lista «records».");
            return;
        }
        if (records.GetArrayLength() > maxRecords) { Error(o, $"El archivo supera el máximo de {maxRecords} registros."); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var r in records.EnumerateArray())
        {
            index++;
            var where = $"Registro {index}";
            if (r.ValueKind != JsonValueKind.Object) { Error(o, $"{where}: no es un objeto."); continue; }
            var before = o.Errors.Count;
            OnlyKeys(r, o, where, "id", "type", "name", "amount", "currency", "date", "category", "account", "count");
            var id = Str(r, "id", 100, o, where);
            var type = Str(r, "type", 10, o, where);
            var name = Str(r, "name", 90, o, where);
            var currency = Currency(r, o, where);
            var date = Date(r, "date", o, where);
            var category = Str(r, "category", 40, o, where, required: false, allowEmpty: true);
            var account = Str(r, "account", 60, o, where, required: false, allowEmpty: true);
            if (type is not null && type is not ("expense" or "income" or "card" or "loan")) Error(o, $"{where}: tipo «{Trunc(type)}» no válido.");

            string? amountMinor = null;
            if (!r.TryGetProperty("amount", out var amountEl) || amountEl.ValueKind != JsonValueKind.Number)
            {
                Error(o, $"{where}: «amount» debe ser un número.");
            }
            else
            {
                // Se parsea el texto JSON original: nada de double. «amount» es el TOTAL, no la cuota.
                var parsed = Money.ParseDecimalText(amountEl.GetRawText());
                switch (parsed.Status)
                {
                    case DecimalParseStatus.Ok: amountMinor = Money.ToApi(parsed.Minor); break;
                    case DecimalParseStatus.TooManyDecimals: Error(o, $"{where}: el importe tiene más de dos decimales; corregilo en el archivo."); break;
                    case DecimalParseStatus.NotPositive: Error(o, $"{where}: el importe debe ser positivo."); break;
                    case DecimalParseStatus.OutOfRange: Error(o, $"{where}: el importe supera el límite."); break;
                    default: Error(o, $"{where}: importe no válido."); break;
                }
            }
            int count = 1;
            if (!r.TryGetProperty("count", out var countEl) || countEl.ValueKind != JsonValueKind.Number || !countEl.TryGetInt32(out count)
                || count is < 1 or > Money.MaxInstallments || countEl.GetRawText().Contains('.'))
            {
                Error(o, $"{where}: «count» debe ser un entero entre 1 y {Money.MaxInstallments}.");
            }
            var isPlan = type is "card" or "loan";
            if (isPlan && amountMinor is not null && long.Parse(amountMinor, System.Globalization.CultureInfo.InvariantCulture) < count)
                Error(o, $"{where}: hay más cuotas que centavos en el total.");
            if (id is not null && !seen.Add(id)) Error(o, $"{where}: id repetido dentro del archivo.");
            if (o.Errors.Count != before) continue;

            var fields = new Dictionary<string, string?>
            {
                ["type"] = type, ["concept"] = name, ["amountMinor"] = amountMinor, ["currency"] = currency, ["date"] = date,
                ["category"] = string.IsNullOrEmpty(category) ? null : category,
                ["account"] = string.IsNullOrEmpty(account) ? null : account,
                ["count"] = isPlan ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "1",
            };
            o.Records.Add(new NormalizedRecord("legacy_v1", id!, isPlan ? "plan" : "entry", Hash(fields), fields));
        }
        // Siempre: aunque el archivo solo tenga ingresos y gastos, tampoco trae saldos de cuentas.
        o.Warnings.Add(LegacyLimitsWarning);
        if (o.Records.Any(r => r.Target == "plan"))
            o.Warnings.Add("Las cuotas se importan como pendientes, aunque sean de meses pasados, con vencimientos calculados desde la fecha del registro original.");
        o.Warnings.Add("El archivo original está en texto claro. Cuando termines, borrá las copias que no necesites.");
    }

    public const string LegacyLimitsWarning =
        "El respaldo original no conoce saldos, pagos ni vencimientos exactos: las cuentas se crean sin saldo inicial, no se registra ningún pago y las fechas de las cuotas son estimadas.";

    private static readonly string[] ExportKeys = ["format", "version", "exportedAt", "accounts", "categories", "entries", "plans", "payments", "transfers"];

    private static void ParseExport(JsonElement root, int maxRecords, ParseOutcome o)
    {
        o.Format = ExportFormat;
        o.FormatVersion = 2;
        OnlyKeys(root, o, "Archivo", ExportKeys);
        if (!root.TryGetProperty("version", out var ver) || ver.GetRawText() != "2") { Error(o, "Versión de exportación no soportada (se espera 2)."); return; }
        var total = 0;
        foreach (var k in ExportKeys[3..])
        {
            if (!root.TryGetProperty(k, out var arr) || arr.ValueKind != JsonValueKind.Array) { Error(o, $"Falta la lista «{k}»."); return; }
            total += arr.GetArrayLength();
        }
        if (total > maxRecords) { Error(o, $"El archivo supera el máximo de {maxRecords} registros."); return; }

        Each(root, "accounts", o, (el, where) =>
        {
            OnlyKeys(el, o, where, "id", "name", "kind", "currency", "last4", "openingBalanceMinor", "openingDate", "deleted");
            var kind = Str(el, "kind", 16, o, where);
            if (kind is not null && !ApiMaps.AccountKinds.ContainsKey(kind)) Error(o, $"{where}: tipo de cuenta no válido.");
            var last4 = Str(el, "last4", 4, o, where, required: false);
            if (last4 is not null && (last4.Length != 4 || !last4.All(char.IsAsciiDigit))) Error(o, $"{where}: últimos cuatro dígitos no válidos.");
            var opening = Str(el, "openingBalanceMinor", 19, o, where, required: false);
            if (opening is not null && !SignedMinor.TryParse(opening, out _)) Error(o, $"{where}: saldo inicial no válido.");
            return new()
            {
                ["name"] = Str(el, "name", 60, o, where), ["kind"] = kind, ["currency"] = Currency(el, o, where), ["last4"] = last4,
                ["openingBalanceMinor"] = opening, ["openingDate"] = Date(el, "openingDate", o, where, required: opening is not null), ["deleted"] = Bool(el, "deleted"),
            };
        }, "account");
        Each(root, "categories", o, (el, where) =>
        {
            OnlyKeys(el, o, where, "id", "name", "kind", "deleted");
            var kind = Str(el, "kind", 16, o, where);
            if (kind is not null && !ApiMaps.CategoryKinds.ContainsKey(kind)) Error(o, $"{where}: tipo de categoría no válido.");
            return new() { ["name"] = Str(el, "name", 40, o, where), ["kind"] = kind, ["deleted"] = Bool(el, "deleted") };
        }, "category");
        Each(root, "entries", o, (el, where) =>
        {
            OnlyKeys(el, o, where, "id", "kind", "concept", "note", "amountMinor", "currency", "date", "accountId", "categoryId", "deleted");
            var kind = Str(el, "kind", 10, o, where);
            if (kind is not null && !ApiMaps.EntryKinds.ContainsKey(kind)) Error(o, $"{where}: tipo no válido.");
            return new()
            {
                ["kind"] = kind, ["concept"] = Str(el, "concept", 90, o, where), ["note"] = Str(el, "note", 500, o, where, required: false),
                ["amountMinor"] = MinorString(el, "amountMinor", o, where), ["currency"] = Currency(el, o, where), ["date"] = Date(el, "date", o, where),
                ["accountId"] = Str(el, "accountId", 100, o, where, required: false), ["categoryId"] = Str(el, "categoryId", 100, o, where, required: false),
                ["deleted"] = Bool(el, "deleted"),
            };
        }, "entry");
        Each(root, "plans", o, (el, where) =>
        {
            OnlyKeys(el, o, where, "id", "kind", "concept", "note", "totalMinor", "count", "currency", "firstDueDate", "accountId", "categoryId",
                "paidBeforeCount", "remainingOnly", "disbursementMinor", "disbursementDate", "disbursementAccountId", "installments", "deleted");
            var kind = Str(el, "kind", 10, o, where);
            if (kind is not null && !ApiMaps.PlanKinds.ContainsKey(kind)) Error(o, $"{where}: tipo de plan no válido.");
            var count = Int(el, "count", 1, Money.MaxInstallments, o, where);
            var paidBefore = Int(el, "paidBeforeCount", 0, Money.MaxInstallments - 1, o, where);
            var total = MinorString(el, "totalMinor", o, where);
            var amounts = new List<string>();
            if (!el.TryGetProperty("installments", out var inst) || inst.ValueKind != JsonValueKind.Array) Error(o, $"{where}: faltan las cuotas.");
            else
            {
                foreach (var i in inst.EnumerateArray())
                {
                    if (i.ValueKind != JsonValueKind.Object) { Error(o, $"{where}: cuota no válida."); continue; }
                    OnlyKeys(i, o, where + " cuota", "number", "amountMinor", "dueDate", "paidMinor", "declaredPaid");
                    amounts.Add(MinorString(i, "amountMinor", o, where) ?? "0");
                }
                if (count is not null && amounts.Count != int.Parse(count, System.Globalization.CultureInfo.InvariantCulture)) Error(o, $"{where}: la cantidad de cuotas no coincide.");
                if (total is not null && amounts.All(a => a != "0")
                    && Money.Sum(amounts.Select(a => long.Parse(a, System.Globalization.CultureInfo.InvariantCulture))) != long.Parse(total, System.Globalization.CultureInfo.InvariantCulture))
                    Error(o, $"{where}: la suma de cuotas no coincide con el total.");
            }
            return new()
            {
                ["kind"] = kind, ["concept"] = Str(el, "concept", 90, o, where), ["note"] = Str(el, "note", 500, o, where, required: false),
                ["totalMinor"] = total, ["count"] = count, ["currency"] = Currency(el, o, where), ["firstDueDate"] = Date(el, "firstDueDate", o, where),
                ["accountId"] = Str(el, "accountId", 100, o, where, required: false), ["categoryId"] = Str(el, "categoryId", 100, o, where, required: false),
                ["paidBeforeCount"] = paidBefore, ["remainingOnly"] = Bool(el, "remainingOnly"),
                ["disbursementMinor"] = MinorString(el, "disbursementMinor", o, where, required: false),
                ["disbursementDate"] = Date(el, "disbursementDate", o, where, required: false),
                ["disbursementAccountId"] = Str(el, "disbursementAccountId", 100, o, where, required: false),
                ["installmentAmounts"] = string.Join(',', amounts),
                ["deleted"] = Bool(el, "deleted"),
            };
        }, "plan");
        Each(root, "payments", o, (el, where) =>
        {
            OnlyKeys(el, o, where, "id", "sourceAccountId", "date", "currency", "amountMinor", "note", "allocations");
            var allocations = new List<string>();
            if (!el.TryGetProperty("allocations", out var al) || al.ValueKind != JsonValueKind.Array || al.GetArrayLength() == 0) Error(o, $"{where}: faltan asignaciones.");
            else
            {
                foreach (var a in al.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) { Error(o, $"{where}: asignación no válida."); continue; }
                    OnlyKeys(a, o, where + " asignación", "planId", "installmentNumber", "entryId", "amountMinor");
                    var amt = MinorString(a, "amountMinor", o, where);
                    var entryId = SafeId(Str(a, "entryId", 100, o, where, required: false), o, where);
                    var planId = SafeId(Str(a, "planId", 100, o, where, required: false), o, where);
                    var number = Int(a, "installmentNumber", 1, Money.MaxInstallments, o, where, required: planId is not null);
                    if ((entryId is null) == (planId is null)) Error(o, $"{where}: cada asignación va a una cuota o a un gasto.");
                    allocations.Add(entryId is not null ? $"e:{entryId}:{amt}" : $"i:{planId}:{number}:{amt}");
                }
            }
            return new()
            {
                ["sourceAccountId"] = Str(el, "sourceAccountId", 100, o, where), ["date"] = Date(el, "date", o, where), ["currency"] = Currency(el, o, where),
                ["amountMinor"] = MinorString(el, "amountMinor", o, where), ["note"] = Str(el, "note", 200, o, where, required: false),
                ["allocations"] = string.Join('|', allocations),
            };
        }, "payment");
        Each(root, "transfers", o, (el, where) =>
        {
            OnlyKeys(el, o, where, "id", "fromAccountId", "toAccountId", "currency", "amountMinor", "date", "note", "deleted");
            return new()
            {
                ["fromAccountId"] = Str(el, "fromAccountId", 100, o, where), ["toAccountId"] = Str(el, "toAccountId", 100, o, where),
                ["currency"] = Currency(el, o, where), ["amountMinor"] = MinorString(el, "amountMinor", o, where), ["date"] = Date(el, "date", o, where),
                ["note"] = Str(el, "note", 200, o, where, required: false), ["deleted"] = Bool(el, "deleted"),
            };
        }, "transfer");
        // Reglas entre registros (moneda, tarjetas, transferencias, pagos): solo si cada registro es válido por sí mismo.
        if (o.Errors.Count == 0)
        {
            foreach (var e in ExportConsistency.Check(o.Records)) Error(o, e);
        }
    }

    /// <summary>Identificadores de la exportación: UUID u otro texto simple. Sin «:», «|» ni «,», que separan campos normalizados.</summary>
    private static string? SafeId(string? id, ParseOutcome o, string where)
    {
        if (id is null || id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return id;
        Error(o, $"{where}: identificador «{Trunc(id)}» no válido.");
        return null;
    }

    private static string? Bool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var b) && b.ValueKind == JsonValueKind.True ? "true" : null;

    private static string? Int(JsonElement el, string name, int min, int max, ParseOutcome o, string where, bool required = true)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            if (required) Error(o, $"{where}: falta «{name}».");
            return null;
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < min || n > max || v.GetRawText().Contains('.'))
        {
            Error(o, $"{where}: «{name}» no válido.");
            return null;
        }
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Each(JsonElement root, string list, ParseOutcome o, Func<JsonElement, string, Dictionary<string, string?>> parse, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var el in root.GetProperty(list).EnumerateArray())
        {
            index++;
            var where = $"{list} {index}";
            if (el.ValueKind != JsonValueKind.Object) { Error(o, $"{where}: no es un objeto."); continue; }
            var before = o.Errors.Count;
            var id = SafeId(Str(el, "id", 100, o, where), o, where);
            var fields = parse(el, where);
            if (id is not null && !seen.Add(id)) Error(o, $"{where}: id repetido.");
            if (o.Errors.Count != before) continue;
            o.Records.Add(new NormalizedRecord("export_v2_" + target, id!, target, Hash(fields), fields));
        }
    }
}
