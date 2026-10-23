using System.Data;
using System.Security.Cryptography;
using System.Text;
using AlvarezFinanzas.Application;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;

namespace AlvarezFinanzas.Api;

/// <summary>
/// Movimientos de caja que NO son gasto presupuestario: pagos de tarjeta/cuotas y transferencias entre cuentas propias.
/// </summary>
public static class CashEndpoints
{
    public sealed record AllocationRequest(Guid? InstallmentId, Guid? EntryId, string? AmountMinor);
    public sealed record PaymentRequest(Guid? SourceAccountId, string? Date, string? Currency, string? AmountMinor,
        Guid? TargetAccountId, Guid? PlanId, List<AllocationRequest>? Allocations, string? Note);
    public sealed record TransferRequest(Guid? FromAccountId, Guid? ToAccountId, string? Currency, string? AmountMinor, string? Date, string? Note);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/obligations", Obligations);
        var p = api.MapGroup("/payments");
        p.MapGet("/", ListPayments);
        p.MapPost("/", CreatePayment);
        p.MapDelete("/{id:guid}", ReversePayment);

        var t = api.MapGroup("/transfers");
        t.MapGet("/", ListTransfers);
        t.MapPost("/", CreateTransfer);
        t.MapDelete("/{id:guid}", DeleteTransfer);
        t.MapPost("/{id:guid}/restore", RestoreTransfer);
    }

    private sealed record Obligation(string Type, Guid Id, string Concept, DateOnly DueDate, long AmountMinor, long PendingMinor, Guid? PlanId, int? Number, int? Count);

    private static object ObligationDto(Obligation o) => new
    {
        type = o.Type, id = o.Id, concept = o.Concept, dueDate = o.DueDate.Api(), amountMinor = Money.ToApi(o.AmountMinor),
        pendingMinor = Money.ToApi(o.PendingMinor), planId = o.PlanId, number = o.Number, count = o.Count,
    };

    /// <summary>Obligaciones pendientes de una tarjeta (cuotas de sus planes + gastos cargados a ella) o de un plan.</summary>
    private static async Task<List<Obligation>> PendingAsync(AppDbContext db, Guid owner, Guid? cardAccountId, Guid? planId, Currency currency, CancellationToken ct)
    {
        var inst = db.Installments.Where(i => i.OwnerId == owner && i.Plan!.DeletedAt == null && i.Plan.Currency == currency
                                              && !i.DeclaredPaid && i.PaidMinor < i.AmountMinor);
        if (planId is not null) inst = inst.Where(i => i.PlanId == planId);
        if (cardAccountId is not null) inst = inst.Where(i => i.Plan!.AccountId == cardAccountId);
        var list = await inst.Select(i => new Obligation("installment", i.Id, i.Plan!.Concept, i.DueDate, i.AmountMinor, i.AmountMinor - i.PaidMinor, i.PlanId, i.Number, i.Plan.Count))
            .ToListAsync(ct);
        if (cardAccountId is not null && planId is null)
        {
            var entries = await db.Entries.Where(e => e.OwnerId == owner && e.DeletedAt == null && e.AccountId == cardAccountId
                                                      && e.Kind == EntryKind.Expense && e.Currency == currency && e.PaidMinor < e.AmountMinor)
                .Select(e => new Obligation("entry", e.Id, e.Concept, e.EffectiveDate, e.AmountMinor, e.AmountMinor - e.PaidMinor, null, null, null))
                .ToListAsync(ct);
            list.AddRange(entries);
        }
        return list.OrderBy(o => o.DueDate).ThenBy(o => o.Number ?? 0).ToList();
    }

    private static async Task<IResult> Obligations(HttpContext ctx, AppDbContext db, Guid? accountId, Guid? planId, string? currency, CancellationToken ct)
    {
        var v = new Validation();
        var c = v.Currency("currency", currency);
        if (accountId is null && planId is null) v.Add("accountId", "Indicá una tarjeta o un plan.");
        if (!v.IsValid) return v.Problem();
        var owner = ctx.User.UserId();
        var list = await PendingAsync(db, owner, accountId, planId, c, ct);
        return TypedResults.Ok(list.Select(ObligationDto));
    }

    /// <summary>
    /// Pago con lo que canceló: cada asignación dice a qué cuota (plan, número, vencimiento) o gasto de tarjeta fue.
    /// Un pago revertido conserva su fila (reversedAt) y sus asignaciones para el historial. Si después se acortó el plan
    /// y la cuota dejó de existir, la asignación conserva plan y número (installmentId, vencimiento y «de N» en null).
    /// </summary>
    private static object PaymentDto(Payment p) => new
    {
        id = p.Id,
        sourceAccountId = p.SourceAccountId,
        sourceAccountName = p.SourceAccount?.Name,
        date = p.Date.Api(),
        currency = p.Currency.ToString(),
        amountMinor = Money.ToApi(p.AmountMinor),
        note = p.Note,
        allocations = p.Allocations
            .OrderBy(a => a.Installment?.DueDate ?? a.Entry?.EffectiveDate ?? DateOnly.MaxValue)
            .ThenBy(a => a.Installment?.Number ?? a.InstallmentNumber ?? 0)
            .Select(a =>
            {
                var plan = a.Installment?.Plan ?? a.Plan;
                return new
                {
                    installmentId = a.InstallmentId,
                    entryId = a.EntryId,
                    amountMinor = Money.ToApi(a.AmountMinor),
                    planId = a.Installment?.PlanId ?? a.PlanId,
                    planKind = plan?.Kind.Api(),
                    concept = plan?.Concept ?? a.Entry?.Concept,
                    installmentNumber = a.Installment?.Number ?? a.InstallmentNumber,
                    installmentCount = a.Installment?.Plan?.Count,
                    dueDate = a.Installment?.DueDate.Api() ?? a.Entry?.EffectiveDate.Api(),
                };
            }),
        version = p.Version,
        createdAt = p.CreatedAt,
        reversedAt = p.DeletedAt,
        deletedAt = p.DeletedAt,
    };

    private static IQueryable<Payment> WithDetails(IQueryable<Payment> q) =>
        q.Include(p => p.SourceAccount)
            .Include(p => p.Allocations).ThenInclude(a => a.Installment).ThenInclude(i => i!.Plan)
            .Include(p => p.Allocations).ThenInclude(a => a.Plan)
            .Include(p => p.Allocations).ThenInclude(a => a.Entry);

    public const int PaymentsMaxPageSize = 200;

    /// <summary>
    /// Historial de pagos del propietario, paginado como /entries: {items, page, pageSize, total}, por fecha, alta e id
    /// descendentes. Filtros: cuenta de origen, plan, cuota, gasto de tarjeta, tarjeta destino, tipo (card: cuotas de
    /// tarjeta y consumos; loan: cuotas de préstamos), moneda y fechas. Por defecto solo vigentes.
    /// El plan de una asignación sale de su cuota o, si la cuota ya no existe (pago revertido y plan acortado), de la
    /// copia guardada en la asignación: así el pago revertido sigue apareciendo en el historial de su plan y su tarjeta.
    /// </summary>
    private static async Task<IResult> ListPayments(HttpContext ctx, AppDbContext db, string? from, string? to, Guid? accountId,
        Guid? planId, Guid? installmentId, Guid? entryId, Guid? targetAccountId, string? kind, string? currency, bool? includeReversed,
        int? page, int? pageSize, CancellationToken ct)
    {
        var v = new Validation();
        var owner = ctx.User.UserId();
        var q = db.Payments.AsNoTracking().Where(p => p.OwnerId == owner);
        if (includeReversed != true) q = q.Where(p => p.DeletedAt == null);
        if (from is not null) { var f = v.Date("from", from); q = q.Where(p => p.Date >= f); }
        if (to is not null) { var t = v.Date("to", to); q = q.Where(p => p.Date <= t); }
        if (currency is not null) { var c = v.Currency("currency", currency); q = q.Where(p => p.Currency == c); }
        if (accountId is not null) q = q.Where(p => p.SourceAccountId == accountId);
        if (planId is not null)
            q = q.Where(p => p.Allocations.Any(a => a.PlanId == planId || (a.Installment != null && a.Installment.PlanId == planId)));
        if (installmentId is not null) q = q.Where(p => p.Allocations.Any(a => a.InstallmentId == installmentId));
        if (entryId is not null) q = q.Where(p => p.Allocations.Any(a => a.EntryId == entryId));
        if (targetAccountId is not null)
            q = q.Where(p => p.Allocations.Any(a => (a.Installment != null && a.Installment.Plan!.AccountId == targetAccountId)
                                                    || (a.Plan != null && a.Plan.AccountId == targetAccountId)
                                                    || (a.Entry != null && a.Entry.AccountId == targetAccountId)));
        if (kind is not null)
        {
            var k = v.Enum("kind", kind, ApiMaps.PlanKinds, "Tipo de plan no válido.");
            q = k == PlanKind.Card
                ? q.Where(p => p.Allocations.Any(a => a.EntryId != null
                                                      || (a.Installment != null && a.Installment.Plan!.Kind == PlanKind.Card)
                                                      || (a.Plan != null && a.Plan.Kind == PlanKind.Card)))
                : q.Where(p => p.Allocations.Any(a => (a.Installment != null && a.Installment.Plan!.Kind == PlanKind.Loan)
                                                      || (a.Plan != null && a.Plan.Kind == PlanKind.Loan)));
        }
        if (!v.IsValid) return v.Problem();
        // Igual que /entries: fuera de rango se ajusta, no da error.
        var number = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 50, 1, PaymentsMaxPageSize);
        var skip = (int)Math.Min((long)(number - 1) * size, int.MaxValue);
        var total = await q.CountAsync(ct);
        var list = await WithDetails(q).AsSplitQuery()
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Skip(skip).Take(size).ToListAsync(ct);
        return TypedResults.Ok(new PageResult<object>(list.Select(PaymentDto).ToList(), number, size, total));
    }

    /// <summary>
    /// Pago transaccional e idempotente (header Idempotency-Key). Reduce caja de la cuenta origen y el pendiente de las
    /// obligaciones; no crea un gasto presupuestario adicional. Las asignaciones nunca superan el pendiente.
    /// </summary>
    private static async Task<IResult> CreatePayment(PaymentRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var v = new Validation();
        var key = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is < 8 or > 80 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            v.Add("idempotencyKey", "Falta un Idempotency-Key válido.");
        var currency = v.Currency("currency", req.Currency);
        var amount = v.Amount("amountMinor", req.AmountMinor);
        var date = v.Date("date", req.Date);
        var note = v.OptionalText("note", req.Note, 200, "La nota");
        if (req.SourceAccountId is null) v.Add("sourceAccountId", "Elegí la cuenta desde la que pagás.");
        if (req.TargetAccountId is null && req.PlanId is null && (req.Allocations is null || req.Allocations.Count == 0))
            v.Add("targetAccountId", "Indicá qué tarjeta, plan o cuotas pagás.");
        if (req.Allocations is { Count: > 500 }) v.Add("allocations", "Demasiadas asignaciones.");
        if (!v.IsValid) return v.Problem();

        var owner = ctx.User.UserId();
        var requestHash = SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(req)));

        var existing = await WithDetails(db.Payments.AsNoTracking())
            .SingleOrDefaultAsync(p => p.OwnerId == owner && p.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            return existing.RequestHash.AsSpan().SequenceEqual(requestHash)
                ? TypedResults.Ok(PaymentDto(existing))
                : Problems.Unprocessable("Esa clave de idempotencia ya se usó con otros datos.");
        }

        return await SerializableAsync(db, async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var source = await Refs.AccountAsync(db, owner, req.SourceAccountId, v, ct, "sourceAccountId");
            if (source is not null)
            {
                if (source.Kind == AccountKind.CreditCard) v.Add("sourceAccountId", "El pago debe salir de una cuenta de caja, banco o billetera.");
                if (source.Currency != currency) v.Add("sourceAccountId", "La cuenta es de otra moneda.");
            }
            if (req.TargetAccountId is not null)
            {
                var target = await Refs.AccountAsync(db, owner, req.TargetAccountId, v, ct, "targetAccountId");
                if (target is not null && target.Kind != AccountKind.CreditCard) v.Add("targetAccountId", "La cuenta destino debe ser una tarjeta.");
            }
            if (req.PlanId is not null && !await db.Plans.AnyAsync(p => p.Id == req.PlanId && p.OwnerId == owner && p.DeletedAt == null, ct))
                v.Add("planId", "El plan no existe.");
            if (!v.IsValid) return v.Problem();

            var payment = new Payment
            {
                OwnerId = owner, SourceAccountId = source!.Id, Date = date, AmountMinor = amount, Currency = currency, Note = note,
                IdempotencyKey = key, RequestHash = requestHash,
            };

            var requested = new List<(string type, Guid id, long amount)>();
            if (req.Allocations is { Count: > 0 })
            {
                foreach (var (a, i) in req.Allocations.Select((a, i) => (a, i)))
                {
                    if ((a.InstallmentId is null) == (a.EntryId is null)) { v.Add($"allocations[{i}]", "Cada asignación va a una cuota o a un gasto."); continue; }
                    var amt = v.Amount($"allocations[{i}].amountMinor", a.AmountMinor);
                    requested.Add(a.InstallmentId is not null ? ("installment", a.InstallmentId.Value, amt) : ("entry", a.EntryId!.Value, amt));
                }
                if (requested.GroupBy(r => r.id).Any(g => g.Count() > 1)) v.Add("allocations", "Hay obligaciones repetidas.");
            }
            else
            {
                // Asignación automática: obligaciones más antiguas primero, hasta cubrir el importe.
                var pending = await PendingAsync(db, owner, req.PlanId is null ? req.TargetAccountId : null, req.PlanId, currency, ct);
                var remaining = amount;
                foreach (var o in pending)
                {
                    if (remaining == 0) break;
                    var take = Math.Min(remaining, o.PendingMinor);
                    requested.Add((o.Type, o.Id, take));
                    remaining -= take;
                }
                // Misma regla y mismo código (422) que una asignación explícita que supera lo pendiente.
                if (remaining > 0) return Problems.Unprocessable("El importe supera lo pendiente de esas obligaciones.");
            }
            if (!v.IsValid) return v.Problem();
            if (Money.Sum(requested.Select(r => r.amount)) != amount) return Problems.Unprocessable("La suma de las asignaciones debe coincidir con el importe del pago.");

            foreach (var (type, id, amt) in requested)
            {
                if (type == "installment")
                {
                    var inst = await db.Installments.Include(i => i.Plan)
                        .SingleOrDefaultAsync(i => i.Id == id && i.OwnerId == owner && i.Plan!.DeletedAt == null, ct);
                    // Referencias como las de sourceAccountId/planId: inexistente, ajena o incompatible → 400 por campo.
                    if (inst is null) { v.Add("allocations", "Alguna cuota no existe."); return v.Problem(); }
                    if (inst.Plan!.Currency != currency) { v.Add("allocations", "Alguna cuota es de otra moneda."); return v.Problem(); }
                    if (amt > inst.PendingMinor) return Problems.Unprocessable("Una asignación supera lo pendiente de la cuota.");
                    inst.PaidMinor = checked(inst.PaidMinor + amt);
                    // El pago también escribe la fila del plan (versión + 1): un borrado o una edición del plan que corre al
                    // mismo tiempo (con la versión anterior) falla con 409 en vez de dejar un plan eliminado con pagos vigentes.
                    TouchPlan(db, inst.Plan!);
                    payment.Allocations.Add(new PaymentAllocation
                    {
                        OwnerId = owner, PaymentId = payment.Id, InstallmentId = id, Installment = inst,
                        PlanId = inst.PlanId, InstallmentNumber = inst.Number, AmountMinor = amt,
                    });
                }
                else
                {
                    var entry = await db.Entries.Include(e => e.Account)
                        .SingleOrDefaultAsync(e => e.Id == id && e.OwnerId == owner && e.DeletedAt == null, ct);
                    if (entry is null || entry.Kind != EntryKind.Expense || entry.Account?.Kind != AccountKind.CreditCard)
                    {
                        v.Add("allocations", "Algún gasto no existe o no está cargado a una tarjeta.");
                        return v.Problem();
                    }
                    if (entry.Currency != currency) { v.Add("allocations", "Algún gasto es de otra moneda."); return v.Problem(); }
                    if (amt > entry.AmountMinor - entry.PaidMinor) return Problems.Unprocessable("Una asignación supera lo pendiente del gasto.");
                    entry.PaidMinor = checked(entry.PaidMinor + amt);
                    payment.Allocations.Add(new PaymentAllocation { OwnerId = owner, PaymentId = payment.Id, EntryId = id, Entry = entry, AmountMinor = amt });
                }
            }

            db.Payments.Add(payment);
            audit.Add(db, "payment.create", "success", "payment", payment.Id);
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (Db.IsRetryableConflict(ex))
            {
                await Db.RollbackQuietlyAsync(tx);
                db.ChangeTracker.Clear();
                return Problems.Conflict(PaymentConflictMessage);
            }
            payment.SourceAccount = source;
            return TypedResults.Created($"/api/v1/payments/{payment.Id}", PaymentDto(payment));
        }, () => Problems.Conflict(PaymentConflictMessage));
    }

    private const string PaymentConflictMessage = "Otro cambio se guardó al mismo tiempo. Revisá lo pendiente y volvé a intentar con el mismo pago.";

    /// <summary>
    /// Transacción Serializable de un pago o una reversión. Los choques al guardar o en el COMMIT se resuelven adentro;
    /// un 40001 en una lectura (antes de guardar) llega como PostgresException y sale de la transacción (que se deshace al
    /// liberarse): se responde el mismo 409, nunca 500.
    /// </summary>
    private static async Task<IResult> SerializableAsync(AppDbContext db, Func<Task<IResult>> body, Func<IResult> onConflict)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(body);
        }
        catch (Exception ex) when (Db.IsRetryableConflict(ex))
        {
            db.ChangeTracker.Clear();
            return onConflict();
        }
    }

    /// <summary>Marca la versión del plan como modificada: SaveChangesAsync la incrementa y la usa como token de concurrencia.</summary>
    internal static void TouchPlan(AppDbContext db, InstallmentPlan plan) => db.Entry(plan).Property(p => p.Version).IsModified = true;

    /// <summary>Reversión explícita de un pago: devuelve el pendiente a las obligaciones y conserva el registro auditado.</summary>
    private static async Task<IResult> ReversePayment(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        return await SerializableAsync(db, async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var payment = await WithDetails(db.Payments)
                .SingleOrDefaultAsync(p => p.Id == id && p.OwnerId == owner && p.DeletedAt == null, ct);
            if (payment is null) return Problems.NotFound();
            if (version is null || payment.Version != version) return Problems.VersionConflict(PaymentDto(payment));
            foreach (var a in payment.Allocations)
            {
                if (a.Installment is { } inst)
                {
                    inst.PaidMinor -= a.AmountMinor;
                    TouchPlan(db, inst.Plan!); // misma regla que al pagar: choca con una edición o un borrado simultáneo del plan
                }
                else if (a.Entry is { } entry) entry.PaidMinor -= a.AmountMinor;
            }
            payment.DeletedAt = clock.GetUtcNow();
            audit.Add(db, "payment.reverse", "success", "payment", id);
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (Db.IsRetryableConflict(ex))
            {
                await Db.RollbackQuietlyAsync(tx);
                db.ChangeTracker.Clear();
                return Problems.VersionConflict();
            }
            return TypedResults.Ok(PaymentDto(payment));
        }, () => Problems.VersionConflict());
    }

    private static object TransferDto(Transfer t) => new
    {
        id = t.Id, fromAccountId = t.FromAccountId, fromAccountName = t.FromAccount?.Name, toAccountId = t.ToAccountId, toAccountName = t.ToAccount?.Name,
        currency = t.Currency.ToString(), amountMinor = Money.ToApi(t.AmountMinor), date = t.Date.Api(), note = t.Note, version = t.Version, deletedAt = t.DeletedAt,
    };

    private static async Task<IResult> ListTransfers(HttpContext ctx, AppDbContext db, string? from, string? to, bool? deleted, CancellationToken ct)
    {
        var v = new Validation();
        var owner = ctx.User.UserId();
        var q = db.Transfers.AsNoTracking().Include(t => t.FromAccount).Include(t => t.ToAccount).Where(t => t.OwnerId == owner);
        q = deleted == true ? q.Where(t => t.DeletedAt != null) : q.Where(t => t.DeletedAt == null);
        if (from is not null) { var f = v.Date("from", from); q = q.Where(t => t.Date >= f); }
        if (to is not null) { var tt = v.Date("to", to); q = q.Where(t => t.Date <= tt); }
        if (!v.IsValid) return v.Problem();
        var list = await q.OrderByDescending(t => t.Date).Take(200).ToListAsync(ct);
        return TypedResults.Ok(list.Select(TransferDto));
    }

    /// <summary>Una sola fila = una operación atómica: no puede quedar una pata sin la otra. Solo misma moneda en v1.</summary>
    private static async Task<IResult> CreateTransfer(TransferRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var v = new Validation();
        var owner = ctx.User.UserId();
        var currency = v.Currency("currency", req.Currency);
        var amount = v.Amount("amountMinor", req.AmountMinor);
        var date = v.Date("date", req.Date);
        var note = v.OptionalText("note", req.Note, 200, "La nota");
        if (req.FromAccountId is null) v.Add("fromAccountId", "Elegí la cuenta de origen.");
        if (req.ToAccountId is null) v.Add("toAccountId", "Elegí la cuenta de destino.");
        if (req.FromAccountId is not null && req.FromAccountId == req.ToAccountId) v.Add("toAccountId", "Origen y destino deben ser distintos.");
        var from = await Refs.AccountAsync(db, owner, req.FromAccountId, v, ct, "fromAccountId");
        var to = await Refs.AccountAsync(db, owner, req.ToAccountId, v, ct, "toAccountId");
        if (from is not null && to is not null)
        {
            if (from.Currency != to.Currency || from.Currency != currency)
                v.Add("toAccountId", "Las transferencias entre monedas distintas no están soportadas todavía.");
            if (from.Kind == AccountKind.CreditCard || to.Kind == AccountKind.CreditCard)
                v.Add("toAccountId", "Para tarjetas usá «Registrar pago».");
        }
        if (!v.IsValid) return v.Problem();
        var transfer = new Transfer { OwnerId = owner, FromAccountId = from!.Id, ToAccountId = to!.Id, Currency = currency, AmountMinor = amount, Date = date, Note = note, FromAccount = from, ToAccount = to };
        db.Transfers.Add(transfer);
        audit.Add(db, "transfer.create", "success", "transfer", transfer.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/transfers/{transfer.Id}", TransferDto(transfer));
    }

    private static async Task<IResult> DeleteTransfer(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var t = await db.Transfers.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner && x.DeletedAt == null, ct);
        if (t is null) return Problems.NotFound();
        if (version is null || t.Version != version) return Problems.VersionConflict(TransferDto(t));
        t.DeletedAt = clock.GetUtcNow();
        audit.Add(db, "transfer.delete", "success", "transfer", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(TransferDto(t)) : Problems.VersionConflict();
    }

    /// <summary>Restaura desde la papelera. Igual que el borrado, exige la versión actual (?version=).</summary>
    private static async Task<IResult> RestoreTransfer(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var t = await db.Transfers.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == owner && x.DeletedAt != null, ct);
        if (t is null) return Problems.NotFound();
        if (version is null || t.Version != version) return Problems.VersionConflict(TransferDto(t));
        t.DeletedAt = null;
        audit.Add(db, "transfer.restore", "success", "transfer", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(TransferDto(t)) : Problems.VersionConflict();
    }
}
