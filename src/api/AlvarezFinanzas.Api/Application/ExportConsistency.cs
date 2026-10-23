using System.Globalization;
using AlvarezFinanzas.Domain;

namespace AlvarezFinanzas.Application;

/// <summary>
/// Reglas entre registros de una exportación v2: las mismas que aplican los endpoints normales al crear cada registro
/// (moneda coherente con la cuenta, ingresos fuera de tarjetas, transferencias de misma moneda y sin tarjetas, pagos
/// desde caja/banco/billetera a cuotas o consumos de tarjeta de la misma moneda, sin superar lo pendiente).
/// Un archivo editado a mano o fabricado que rompa alguna se rechaza ENTERO: nunca se suman monedas distintas.
/// Se ejecuta en la vista previa (422 con la lista de errores) y otra vez al confirmar, antes de escribir nada.
/// </summary>
public static class ExportConsistency
{
    private const int MaxErrors = 50;

    private sealed record AccountRef(string Kind, string Currency, string Name, bool Deleted);
    private sealed record EntryRef(string Kind, string Currency, string? AccountId, long Amount, bool Deleted);
    private sealed record PlanRef(string Kind, string Currency, int Count, int PaidBefore, long[] Amounts, bool Deleted);

    public static List<string> Check(IReadOnlyList<NormalizedRecord> records)
    {
        var errors = new List<string>();
        void Error(string message)
        {
            if (errors.Count < MaxErrors) errors.Add(message);
        }

        IEnumerable<(NormalizedRecord r, string where)> Of(string target, string list) =>
            records.Where(r => r.Target == target).Select((r, i) => (r, $"{list} {i + 1}"));
        static string? F(NormalizedRecord r, string key) => r.Fields.GetValueOrDefault(key);
        static bool Deleted(NormalizedRecord r) => F(r, "deleted") == "true";
        static long M(string s) => long.Parse(s, CultureInfo.InvariantCulture);
        static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        var accounts = new Dictionary<string, AccountRef>(StringComparer.Ordinal);
        // Igual que los índices únicos de la base (nombre exacto): dos activas iguales no pueden venir de esta app.
        var activeAccountNames = new HashSet<(string, string)>();
        foreach (var (r, where) in Of("account", "accounts"))
        {
            var a = new AccountRef(F(r, "kind")!, F(r, "currency")!, F(r, "name")!, Deleted(r));
            accounts[r.SourceId] = a;
            if (a.Kind == "credit_card" && F(r, "openingBalanceMinor") is not null) Error($"{where}: una tarjeta de crédito no lleva saldo inicial.");
            if ((F(r, "openingBalanceMinor") is null) != (F(r, "openingDate") is null)) Error($"{where}: el saldo inicial y su fecha van juntos.");
            if (!a.Deleted && !activeAccountNames.Add((a.Name, a.Currency))) Error($"{where}: hay dos cuentas activas con el mismo nombre y moneda.");
        }

        var categories = new HashSet<string>(StringComparer.Ordinal);
        var activeCategoryNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (r, where) in Of("category", "categories"))
        {
            categories.Add(r.SourceId);
            if (!Deleted(r) && !activeCategoryNames.Add(F(r, "name")!)) Error($"{where}: hay dos categorías activas con el mismo nombre.");
        }

        AccountRef? Account(string? id, string where, string what)
        {
            if (id is null) return null;
            if (accounts.TryGetValue(id, out var a)) return a;
            Error($"{where}: {what} no está en el archivo.");
            return null;
        }

        var entries = new Dictionary<string, EntryRef>(StringComparer.Ordinal);
        foreach (var (r, where) in Of("entry", "entries"))
        {
            var e = new EntryRef(F(r, "kind")!, F(r, "currency")!, F(r, "accountId"), M(F(r, "amountMinor")!), Deleted(r));
            entries[r.SourceId] = e;
            if (F(r, "categoryId") is { } cat && !categories.Contains(cat)) Error($"{where}: la categoría no está en el archivo.");
            var account = Account(e.AccountId, where, "la cuenta");
            if (account is null) continue;
            if (account.Currency != e.Currency) Error($"{where}: la cuenta es de otra moneda.");
            if (account.Kind == "credit_card" && e.Kind == "income") Error($"{where}: un ingreso no puede registrarse en una tarjeta de crédito.");
        }

        var plans = new Dictionary<string, PlanRef>(StringComparer.Ordinal);
        foreach (var (r, where) in Of("plan", "plans"))
        {
            var count = I(F(r, "count")!);
            var paidBefore = F(r, "paidBeforeCount") is { } pb ? I(pb) : 0;
            var amounts = F(r, "installmentAmounts")!.Split(',').Select(M).ToArray();
            var p = new PlanRef(F(r, "kind")!, F(r, "currency")!, count, paidBefore, amounts, Deleted(r));
            plans[r.SourceId] = p;
            if (F(r, "categoryId") is { } cat && !categories.Contains(cat)) Error($"{where}: la categoría no está en el archivo.");
            if (Account(F(r, "accountId"), where, "la cuenta") is { } account && account.Currency != p.Currency) Error($"{where}: la cuenta es de otra moneda.");
            if (paidBefore >= count) Error($"{where}: las cuotas pagadas antes de registrar deben ser menos que el total de cuotas.");
            if (paidBefore > 0 && F(r, "remainingOnly") == "true") Error($"{where}: elegí una sola forma de registrar un crédito en curso.");
            var first = DateOnly.ParseExact(F(r, "firstDueDate")!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!FinanceCalendar.IsInRange(FinanceCalendar.DueDate(first, count))) Error($"{where}: la última cuota queda fuera del rango de fechas permitido.");

            var hasDisbursement = F(r, "disbursementMinor") is not null;
            if (!hasDisbursement && (F(r, "disbursementDate") is not null || F(r, "disbursementAccountId") is not null))
                Error($"{where}: la fecha o la cuenta de acreditación requieren el capital recibido.");
            if (hasDisbursement)
            {
                if (p.Kind != "loan") Error($"{where}: el capital recibido solo aplica a préstamos.");
                if (F(r, "disbursementDate") is null) Error($"{where}: falta la fecha de acreditación del capital.");
                if (Account(F(r, "disbursementAccountId"), where, "la cuenta de acreditación") is { } d)
                {
                    if (d.Kind == "credit_card") Error($"{where}: el capital no puede acreditarse en una tarjeta.");
                    if (d.Currency != p.Currency) Error($"{where}: la cuenta de acreditación es de otra moneda.");
                }
            }
        }

        // Lo ya asignado por pagos anteriores del archivo: ninguna obligación puede quedar pagada de más.
        var entryPaid = new Dictionary<string, long>(StringComparer.Ordinal);
        var installmentPaid = new Dictionary<(string, int), long>();
        foreach (var (r, where) in Of("payment", "payments"))
        {
            var currency = F(r, "currency")!;
            if (Account(F(r, "sourceAccountId"), where, "la cuenta de origen") is { } source)
            {
                if (source.Kind == "credit_card") Error($"{where}: el pago debe salir de una cuenta de caja, banco o billetera.");
                if (source.Currency != currency) Error($"{where}: la cuenta de origen es de otra moneda.");
            }
            long sum = 0;
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in F(r, "allocations")!.Split('|'))
            {
                var bits = part.Split(':');
                var amount = M(bits[^1]);
                sum = checked(sum + amount);
                if (!targets.Add(string.Join(':', bits[..^1]))) Error($"{where}: hay obligaciones repetidas en el mismo pago.");
                if (bits[0] == "e")
                {
                    if (!entries.TryGetValue(bits[1], out var e)) { Error($"{where}: un gasto pagado no está en el archivo."); continue; }
                    if (e.Kind != "expense" || e.AccountId is null || !accounts.TryGetValue(e.AccountId, out var card) || card.Kind != "credit_card")
                        Error($"{where}: solo se pagan gastos cargados a una tarjeta de crédito.");
                    if (e.Currency != currency) Error($"{where}: un gasto pagado es de otra moneda.");
                    if (e.Deleted) Error($"{where}: un gasto pagado está eliminado.");
                    var paid = checked(entryPaid.GetValueOrDefault(bits[1]) + amount);
                    entryPaid[bits[1]] = paid;
                    if (paid > e.Amount) Error($"{where}: los pagos superan lo pendiente de un gasto.");
                }
                else
                {
                    if (!plans.TryGetValue(bits[1], out var p)) { Error($"{where}: un plan pagado no está en el archivo."); continue; }
                    var number = I(bits[2]);
                    if (p.Currency != currency) Error($"{where}: una cuota pagada es de otra moneda.");
                    if (p.Deleted) Error($"{where}: una cuota pagada pertenece a un plan eliminado.");
                    if (number > p.Count) { Error($"{where}: una cuota pagada no existe en su plan."); continue; }
                    if (number <= p.PaidBefore) Error($"{where}: una cuota declarada pagada antes de registrar no admite pagos.");
                    var paid = checked(installmentPaid.GetValueOrDefault((bits[1], number)) + amount);
                    installmentPaid[(bits[1], number)] = paid;
                    if (paid > p.Amounts[number - 1]) Error($"{where}: los pagos superan lo pendiente de una cuota.");
                }
            }
            if (sum != M(F(r, "amountMinor")!)) Error($"{where}: el importe no coincide con la suma de sus asignaciones.");
        }

        foreach (var (r, where) in Of("transfer", "transfers"))
        {
            var from = Account(F(r, "fromAccountId"), where, "la cuenta de origen");
            var to = Account(F(r, "toAccountId"), where, "la cuenta de destino");
            if (F(r, "fromAccountId") == F(r, "toAccountId")) Error($"{where}: origen y destino deben ser distintos.");
            if (from is null || to is null) continue;
            var currency = F(r, "currency");
            if (from.Currency != currency || to.Currency != currency)
                Error($"{where}: las transferencias entre monedas distintas no están soportadas.");
            if (from.Kind == "credit_card" || to.Kind == "credit_card")
                Error($"{where}: una transferencia no puede entrar ni salir de una tarjeta de crédito.");
        }
        return errors;
    }
}
