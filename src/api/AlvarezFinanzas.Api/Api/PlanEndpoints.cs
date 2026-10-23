using AlvarezFinanzas.Application;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;

namespace AlvarezFinanzas.Api;

/// <summary>Compras en cuotas y planes de pago de préstamos.</summary>
public static class PlanEndpoints
{
    /// <summary>amountMode: "total" (total de TODAS las cuotas) o "installment" (cuota mensual × cantidad).</summary>
    public sealed record PlanRequest(string? Kind, string? Concept, string? AmountMode, string? AmountMinor, int? Count, string? Currency,
        string? FirstDueDate, Guid? AccountId, Guid? CategoryId, string? Note, int? PaidBeforeCount, bool? RemainingOnly,
        string? DisbursementMinor, string? DisbursementDate, Guid? DisbursementAccountId, long? Version);

    public sealed record PreviewRequest(string? AmountMode, string? AmountMinor, int? Count, string? FirstDueDate);
    public sealed record AdjustRequest(string? PendingTotalMinor, long? Version);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/plans");
        g.MapPost("/preview", Preview);
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapGet("/{id:guid}/installments", Installments);
        g.MapPost("/", Create);
        g.MapPatch("/{id:guid}", Update);
        g.MapPost("/{id:guid}/adjust-pending", AdjustPending);
        g.MapDelete("/{id:guid}", Delete);
        g.MapPost("/{id:guid}/restore", Restore);
    }

    public static object InstallmentDto(Installment i) => new
    {
        id = i.Id,
        number = i.Number,
        amountMinor = Money.ToApi(i.AmountMinor),
        paidMinor = Money.ToApi(i.DeclaredPaid ? i.AmountMinor : i.PaidMinor),
        pendingMinor = Money.ToApi(i.PendingMinor),
        dueDate = i.DueDate.Api(),
        declaredPaid = i.DeclaredPaid,
        status = i.DeclaredPaid ? "declared_paid" : i.PaidMinor == 0 ? "pending" : i.PaidMinor < i.AmountMinor ? "partial" : "paid",
    };

    public static object Dto(InstallmentPlan p, DateOnly today, bool includeInstallments = false)
    {
        var inst = p.Installments.OrderBy(i => i.Number).ToList();
        var paid = Money.Sum(inst.Select(i => i.DeclaredPaid ? i.AmountMinor : i.PaidMinor));
        var next = inst.FirstOrDefault(i => i.PendingMinor > 0);
        var current = inst.LastOrDefault(i => i.DueDate <= today);
        return new
        {
            id = p.Id,
            kind = p.Kind.Api(),
            concept = p.Concept,
            note = p.Note,
            totalMinor = Money.ToApi(p.TotalMinor),
            count = p.Count,
            currency = p.Currency.ToString(),
            firstDueDate = p.FirstDueDate.Api(),
            lastDueDate = inst.Count > 0 ? inst[^1].DueDate.Api() : null,
            accountId = p.AccountId,
            accountName = p.Account?.Name,
            categoryId = p.CategoryId,
            categoryName = p.Category?.Name,
            paidBeforeCount = p.PaidBeforeCount,
            remainingOnly = p.RemainingOnly,
            disbursementMinor = p.DisbursementMinor is { } d ? Money.ToApi(d) : null,
            disbursementDate = p.DisbursementDate.Api(),
            disbursementAccountId = p.DisbursementAccountId,
            paidMinor = Money.ToApi(paid),
            pendingMinor = Money.ToApi(p.TotalMinor - paid),
            currentInstallment = current?.Number ?? 0,
            remainingInstallments = inst.Count(i => i.DueDate > today),
            nextDue = next is null ? null : new { number = next.Number, dueDate = next.DueDate.Api(), pendingMinor = Money.ToApi(next.PendingMinor) },
            hasPayments = HasAppliedPayments(p), // bloqueo del cronograma en la UI: misma condición que Update y Delete
            finished = inst.All(i => i.PendingMinor == 0),
            version = p.Version,
            deletedAt = p.DeletedAt,
            installments = includeInstallments ? inst.Select(InstallmentDto).ToList() : null,
        };
    }

    private static bool TryResolveTotal(Validation v, string? mode, string? amount, int? count, out long total)
    {
        total = 0;
        var amountMinor = v.Amount("amountMinor", amount);
        if (count is null or < 1 or > Money.MaxInstallments) v.Add("count", $"La cantidad de cuotas debe estar entre 1 y {Money.MaxInstallments}.");
        if (mode is not ("total" or "installment")) v.Add("amountMode", "Indicá si el importe es el total o la cuota.");
        if (!v.IsValid) return false;
        if (mode == "installment")
        {
            try { total = checked(amountMinor * count!.Value); }
            catch (OverflowException) { v.Add("amountMinor", "El total supera el límite permitido."); return false; }
            if (total > Money.MaxAmountMinor) { v.Add("amountMinor", "El total supera el límite permitido."); return false; }
        }
        else
        {
            total = amountMinor;
        }
        if (count > total)
        {
            v.Add("count", "Hay más cuotas que centavos en el total: cada cuota debe ser de al menos 0,01.");
            return false;
        }
        return true;
    }

    private static IResult Preview(PreviewRequest req)
    {
        var v = new Validation();
        var first = v.Date("firstDueDate", req.FirstDueDate, "La fecha de primera cuota");
        if (!TryResolveTotal(v, req.AmountMode, req.AmountMinor, req.Count, out var total)) return v.Problem();
        if (!v.IsValid) return v.Problem();
        var amounts = Money.Distribute(total, req.Count!.Value);
        var lastDue = FinanceCalendar.DueDate(first, req.Count.Value);
        // Misma regla y mismo código (400 por campo) que al crear o editar el plan.
        if (!FinanceCalendar.IsInRange(lastDue))
        {
            v.Add("firstDueDate", "La última cuota queda fuera del rango de fechas permitido.");
            return v.Problem();
        }
        return TypedResults.Ok(new
        {
            totalMinor = Money.ToApi(total),
            count = req.Count,
            baseMinor = Money.ToApi(total / req.Count.Value),
            remainderMinor = Money.ToApi(total % req.Count.Value),
            installments = amounts.Select((a, i) => new { number = i + 1, amountMinor = Money.ToApi(a), dueDate = FinanceCalendar.DueDate(first, i + 1).Api() }),
        });
    }

    private static async Task<IResult> List(HttpContext ctx, AppDbContext db, TimeProvider clock, string? kind, string? currency, string? status, bool? deleted, CancellationToken ct)
    {
        var v = new Validation();
        var owner = ctx.User.UserId();
        var q = db.Plans.AsNoTracking().AsSplitQuery().Include(p => p.Installments).Include(p => p.Account).Include(p => p.Category)
            .Where(p => p.OwnerId == owner);
        q = deleted == true ? q.Where(p => p.DeletedAt != null) : q.Where(p => p.DeletedAt == null);
        if (kind is not null) { var k = v.Enum("kind", kind, ApiMaps.PlanKinds, "Tipo de plan no válido."); q = q.Where(p => p.Kind == k); }
        if (currency is not null) { var c = v.Currency("currency", currency); q = q.Where(p => p.Currency == c); }
        if (!v.IsValid) return v.Problem();
        var list = await q.OrderByDescending(p => p.FirstDueDate).Take(500).ToListAsync(ct);
        var today = FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone);
        var dtos = list.Select(p => (p, finished: p.Installments.All(i => i.PendingMinor == 0)))
            .Where(x => status switch { "active" => !x.finished, "finished" => x.finished, _ => true })
            .Select(x => Dto(x.p, today));
        return TypedResults.Ok(dtos);
    }

    private static async Task<InstallmentPlan?> LoadAsync(AppDbContext db, Guid owner, Guid id, bool? deleted, CancellationToken ct, bool tracking = false)
    {
        var q = tracking ? db.Plans : db.Plans.AsNoTracking();
        q = q.Include(p => p.Installments).Include(p => p.Account).Include(p => p.Category).Where(p => p.Id == id && p.OwnerId == owner);
        if (deleted is not null) q = deleted.Value ? q.Where(p => p.DeletedAt != null) : q.Where(p => p.DeletedAt == null);
        return await q.SingleOrDefaultAsync(ct);
    }

    private static async Task<IResult> Get(Guid id, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var plan = await LoadAsync(db, ctx.User.UserId(), id, null, ct);
        return plan is null ? Problems.NotFound() : TypedResults.Ok(Dto(plan, FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone), includeInstallments: true));
    }

    private static async Task<IResult> Installments(Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct)
    {
        var plan = await LoadAsync(db, ctx.User.UserId(), id, null, ct);
        return plan is null ? Problems.NotFound() : TypedResults.Ok(plan.Installments.OrderBy(i => i.Number).Select(InstallmentDto));
    }

    private sealed record ValidatedPlan(PlanKind Kind, string Concept, long Total, int Count, Currency Currency, DateOnly FirstDue,
        Account? Account, Category? Category, string? Note, int PaidBefore, bool RemainingOnly,
        long? Disbursement, DateOnly? DisbursementDate, Account? DisbursementAccount);

    private static async Task<(ValidatedPlan?, Validation)> ValidateAsync(PlanRequest req, Guid owner, AppDbContext db, CancellationToken ct)
    {
        var v = new Validation();
        var kind = v.Enum("kind", req.Kind, ApiMaps.PlanKinds, "Elegí compra en cuotas o préstamo.");
        var concept = v.RequiredText("concept", req.Concept, 90, "El concepto");
        var currency = v.Currency("currency", req.Currency);
        var first = v.Date("firstDueDate", req.FirstDueDate, "La fecha de primera cuota");
        var note = v.OptionalText("note", req.Note, 500, "La nota");
        TryResolveTotal(v, req.AmountMode, req.AmountMinor, req.Count, out var total);
        var account = await Refs.AccountAsync(db, owner, req.AccountId, v, ct);
        var category = await Refs.CategoryAsync(db, owner, req.CategoryId, v, ct);
        var paidBefore = req.PaidBeforeCount ?? 0;
        if (paidBefore < 0 || (req.Count is { } cnt && paidBefore >= cnt)) v.Add("paidBeforeCount", "Las cuotas ya pagadas deben ser menos que el total de cuotas.");
        var remainingOnly = req.RemainingOnly ?? false;
        if (remainingOnly && paidBefore > 0) v.Add("remainingOnly", "Elegí una sola forma de registrar un crédito en curso.");
        long? disbursement = null;
        DateOnly? disbursementDate = null;
        Account? disbursementAccount = null;
        if (!string.IsNullOrEmpty(req.DisbursementMinor))
        {
            if (kind != PlanKind.Loan) v.Add("disbursementMinor", "El capital recibido solo aplica a préstamos.");
            disbursement = v.Amount("disbursementMinor", req.DisbursementMinor, "El capital recibido");
            disbursementDate = v.Date("disbursementDate", req.DisbursementDate, "La fecha de acreditación");
            disbursementAccount = await Refs.AccountAsync(db, owner, req.DisbursementAccountId, v, ct, "disbursementAccountId");
            if (disbursementAccount is { Kind: AccountKind.CreditCard }) v.Add("disbursementAccountId", "El capital no puede acreditarse en una tarjeta.");
            if (disbursementAccount is not null && disbursementAccount.Currency != currency) v.Add("disbursementAccountId", "La cuenta es de otra moneda.");
        }
        if (account is not null && account.Currency != currency) v.Add("accountId", "La cuenta es de otra moneda.");
        if (v.IsValid && !FinanceCalendar.IsInRange(FinanceCalendar.DueDate(first, req.Count!.Value)))
            v.Add("firstDueDate", "La última cuota queda fuera del rango de fechas permitido.");
        return v.IsValid
            ? (new ValidatedPlan(kind, concept!, total, req.Count!.Value, currency, first, account, category, note, paidBefore, remainingOnly, disbursement, disbursementDate, disbursementAccount), v)
            : (null, v);
    }

    private static async Task<IResult> Create(PlanRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var (val, v) = await ValidateAsync(req, owner, db, ct);
        if (val is null) return v.Problem();
        var plan = new InstallmentPlan
        {
            OwnerId = owner, Kind = val.Kind, Concept = val.Concept, TotalMinor = val.Total, Count = val.Count, Currency = val.Currency,
            FirstDueDate = val.FirstDue, AccountId = val.Account?.Id, Account = val.Account, CategoryId = val.Category?.Id, Category = val.Category,
            Note = val.Note, PaidBeforeCount = val.PaidBefore, RemainingOnly = val.RemainingOnly,
            DisbursementMinor = val.Disbursement, DisbursementDate = val.DisbursementDate, DisbursementAccountId = val.DisbursementAccount?.Id,
        };
        plan.Installments = PlanSchedule.Build(plan);
        db.Plans.Add(plan);
        audit.Add(db, "plan.create", "success", "plan", plan.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/plans/{plan.Id}", Dto(plan, FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone), true));
    }

    /// <summary>
    /// Edición sin reescribir historia: con pagos aplicados a alguna cuota se bloquea el cambio de total, cantidad,
    /// fecha, moneda o cuotas declaradas pagadas; para eso existe el ajuste explícito de pendientes. Sin pagos aplicados
    /// (nunca pagado, o con todos sus pagos revertidos) el cronograma se recalcula.
    /// </summary>
    private static async Task<IResult> Update(Guid id, PlanRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var (val, v) = await ValidateAsync(req, owner, db, ct);
        if (req.Version is null) v.Add("version", "Falta la versión.");
        if (val is null || !v.IsValid) return v.Problem();
        var plan = await LoadAsync(db, owner, id, false, ct, tracking: true);
        if (plan is null) return Problems.NotFound();
        var today = FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone);
        if (plan.Version != req.Version) return Problems.VersionConflict(Dto(plan, today));
        if (val.Kind != plan.Kind) return Problems.Unprocessable("No se puede cambiar el tipo de plan.");

        var scheduleChanged = val.Total != plan.TotalMinor || val.Count != plan.Count || val.FirstDue != plan.FirstDueDate
                              || val.Currency != plan.Currency || val.PaidBefore != plan.PaidBeforeCount;
        // Misma condición que «hasPayments» del DTO (bloqueo en la UI) y que el borrado: solo cuentan pagos vigentes.
        if (scheduleChanged && HasAppliedPayments(plan))
        {
            return Problems.PaymentsApplied("El plan tiene cuotas pagadas: no se puede modificar total, cantidad, fecha o moneda. Usá «Ajustar pendientes» o revertí los pagos.");
        }

        plan.Concept = val.Concept;
        plan.Note = val.Note;
        plan.AccountId = val.Account?.Id;
        plan.Account = val.Account;
        plan.CategoryId = val.Category?.Id;
        plan.Category = val.Category;
        plan.DisbursementMinor = val.Disbursement;
        plan.DisbursementDate = val.DisbursementDate;
        plan.DisbursementAccountId = val.DisbursementAccount?.Id;
        plan.RemainingOnly = val.RemainingOnly;
        if (scheduleChanged)
        {
            plan.TotalMinor = val.Total;
            plan.Count = val.Count;
            plan.FirstDueDate = val.FirstDue;
            plan.Currency = val.Currency;
            plan.PaidBeforeCount = val.PaidBefore;
            if (!await RebuildScheduleAsync(db, plan, ct))
            {
                db.ChangeTracker.Clear();
                return Problems.PaymentsApplied("El plan tiene pagos aplicados. Revertí esos pagos antes de cambiar el cronograma.");
            }
        }
        db.Entry(plan).Property(p => p.Version).IsModified = true;
        audit.Add(db, "plan.update", "success", "plan", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(Dto(plan, today, true)) : Problems.VersionConflict();
    }

    /// <summary>Hay pagos vigentes aplicados a alguna cuota. Un pago revertido ya devolvió su importe y no cuenta.</summary>
    internal static bool HasAppliedPayments(InstallmentPlan plan) => plan.Installments.Any(i => i.PaidMinor > 0);

    /// <summary>
    /// Recalcula el cronograma de un plan SIN pagos vigentes. Las cuotas que siguen existiendo (mismo número) se
    /// actualizan en su fila; las que sobran se eliminan. Un pago revertido conserva su fila, su auditoría y sus
    /// asignaciones a las cuotas que siguen existiendo; solo se quitan sus asignaciones a cuotas que dejan de existir,
    /// porque ya no tienen a qué apuntar (antes, la FK de esas asignaciones hacía fallar la edición con 500).
    /// </summary>
    private static async Task<bool> RebuildScheduleAsync(AppDbContext db, InstallmentPlan plan, CancellationToken ct)
    {
        var rebuilt = PlanSchedule.Build(plan);
        var current = plan.Installments.ToDictionary(i => i.Number);
        foreach (var fresh in rebuilt)
        {
            if (current.TryGetValue(fresh.Number, out var row))
            {
                row.AmountMinor = fresh.AmountMinor;
                row.DueDate = fresh.DueDate;
                row.DeclaredPaid = fresh.DeclaredPaid;
                row.PaidMinor = 0;
            }
            else
            {
                plan.Installments.Add(fresh);
                db.Installments.Add(fresh);
            }
        }
        var removed = plan.Installments.Where(i => i.Number > plan.Count).ToList();
        if (removed.Count == 0) return true;
        var removedIds = removed.Select(i => i.Id).ToList();
        var stale = await db.PaymentAllocations
            .Where(a => a.OwnerId == plan.OwnerId && a.InstallmentId != null && removedIds.Contains(a.InstallmentId.Value))
            .Include(a => a.Payment)
            .ToListAsync(ct);
        // Defensa: una asignación de un pago vigente nunca se borra (no debería existir con PaidMinor en cero).
        if (stale.Any(a => a.Payment!.DeletedAt is null)) return false;
        db.PaymentAllocations.RemoveRange(stale);
        db.Installments.RemoveRange(removed);
        foreach (var r in removed) plan.Installments.Remove(r);
        return true;
    }

    /// <summary>Redistribuye un nuevo total pendiente solo entre cuotas sin pagos. Las pagadas no se tocan.</summary>
    private static async Task<IResult> AdjustPending(Guid id, AdjustRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var v = new Validation();
        var pendingTotal = v.Amount("pendingTotalMinor", req.PendingTotalMinor, "El total pendiente");
        if (req.Version is null) v.Add("version", "Falta la versión.");
        if (!v.IsValid) return v.Problem();
        var owner = ctx.User.UserId();
        var plan = await LoadAsync(db, owner, id, false, ct, tracking: true);
        if (plan is null) return Problems.NotFound();
        var today = FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone);
        if (plan.Version != req.Version) return Problems.VersionConflict(Dto(plan, today));
        var open = plan.Installments.Where(i => i.PaidMinor == 0 && !i.DeclaredPaid).OrderBy(i => i.Number).ToList();
        if (open.Count == 0) return Problems.Unprocessable("No hay cuotas sin pagos para ajustar.");
        if (open.Count > pendingTotal) return Problems.Unprocessable("El total pendiente es menor que la cantidad de cuotas abiertas.");
        var settled = Money.Sum(plan.Installments.Except(open).Select(i => i.AmountMinor));
        long newTotal;
        try { newTotal = checked(settled + pendingTotal); }
        catch (OverflowException) { return Problems.Unprocessable("El total supera el límite permitido."); }
        if (newTotal > Money.MaxAmountMinor) return Problems.Unprocessable("El total supera el límite permitido.");
        var amounts = Money.Distribute(pendingTotal, open.Count);
        for (var i = 0; i < open.Count; i++) open[i].AmountMinor = amounts[i];
        plan.TotalMinor = newTotal;
        audit.Add(db, "plan.adjust_pending", "success", "plan", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(Dto(plan, today, true)) : Problems.VersionConflict();
    }

    /// <summary>Eliminación reversible. Con pagos aplicados se exige revertirlos antes (no se destruyen pagos).</summary>
    private static async Task<IResult> Delete(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var plan = await LoadAsync(db, owner, id, false, ct, tracking: true);
        if (plan is null) return Problems.NotFound();
        var today = FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone);
        if (version is null || plan.Version != version) return Problems.VersionConflict(Dto(plan, today));
        if (HasAppliedPayments(plan))
            return Problems.PaymentsApplied("El plan tiene pagos aplicados. Revertí esos pagos antes de eliminarlo.");
        plan.DeletedAt = clock.GetUtcNow();
        audit.Add(db, "plan.delete", "success", "plan", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok ? TypedResults.Ok(Dto(plan, today)) : Problems.VersionConflict();
    }

    /// <summary>Restaura desde la papelera. Igual que el borrado, exige la versión actual (?version=).</summary>
    private static async Task<IResult> Restore(Guid id, long? version, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var plan = await LoadAsync(db, owner, id, true, ct, tracking: true);
        if (plan is null) return Problems.NotFound();
        if (version is null || plan.Version != version)
            return Problems.VersionConflict(Dto(plan, FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone)));
        plan.DeletedAt = null;
        audit.Add(db, "plan.restore", "success", "plan", id);
        return await Db.SaveVersionedAsync(db, ct) == SaveOutcome.Ok
            ? TypedResults.Ok(Dto(plan, FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone)))
            : Problems.VersionConflict();
    }
}
