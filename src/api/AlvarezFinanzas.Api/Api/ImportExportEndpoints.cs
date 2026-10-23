using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AlvarezFinanzas.Application;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using AlvarezFinanzas.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlvarezFinanzas.Api;

/// <summary>
/// Importación con vista previa + confirmación (transaccional e idempotente) y exportación versionada autenticada.
/// El archivo no se persiste: solo los datos normalizados de la vista previa, con caducidad.
/// </summary>
public static class ImportExportEndpoints
{
    public sealed record CommitRequest(bool? Confirm, Dictionary<string, string>? AccountKinds);
    private static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public static void Map(RouteGroupBuilder api)
    {
        // El límite global de Kestrel es 64 KiB; solo la vista previa admite archivos (el máximo real lo controla App:ImportMaxBytes).
        api.MapPost("/imports/preview", Preview).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16 * 1024 * 1024));
        api.MapPost("/imports/{id:guid}/commit", Commit);
        api.MapPost("/exports", Export).RequireAuthorization(AuthConstants.PolicyElevated);
    }

    private static int L(string? s) => int.Parse(s!, CultureInfo.InvariantCulture);
    private static long M(string? s) => long.Parse(s!, CultureInfo.InvariantCulture);
    private static DateOnly D(string? s) => DateOnly.ParseExact(s!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<IResult> Preview(HttpContext ctx, AppDbContext db, AttemptLimiter limiter, AuditLog audit, IOptions<AppOptions> options,
        TimeProvider clock, CancellationToken ct)
    {
        var o = options.Value;
        var owner = ctx.User.UserId();
        var limit = limiter.Bulk(owner);
        if (!limit.IsAllowed) return Problems.TooMany(limit.RetryAfter, ctx);
        if (ctx.Request.ContentType is null || !ctx.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return TypedResults.Problem(title: "El archivo debe ser JSON.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        if (ctx.Request.ContentLength > o.ImportMaxBytes)
            return TypedResults.Problem(title: "El archivo supera el tamaño máximo.", statusCode: StatusCodes.Status413PayloadTooLarge);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > o.ImportMaxBytes)
                return TypedResults.Problem(title: "El archivo supera el tamaño máximo.", statusCode: StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var parsed = ImportParser.Parse(bytes, o.ImportMaxRecords);
        if (parsed.Errors.Count > 0)
        {
            await audit.WriteAsync(db, "import.preview", "invalid");
            return TypedResults.Problem(title: "El archivo tiene errores. Corregilos y volvé a intentar.", statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["errors"] = parsed.Errors });
        }

        var sourceIds = parsed.Records.Select(r => r.SourceId).ToList();
        var existing = await db.ImportedRecords.AsNoTracking()
            .Where(r => r.OwnerId == owner && sourceIds.Contains(r.SourceId))
            .Select(r => new { r.SourceType, r.SourceId, r.ContentHash })
            .ToListAsync(ct);
        var existingMap = existing.ToDictionary(e => (e.SourceType, e.SourceId), e => Convert.ToHexString(e.ContentHash));
        var duplicates = 0;
        var conflicts = new List<object>();
        foreach (var r in parsed.Records)
        {
            if (!existingMap.TryGetValue((r.SourceType, r.SourceId), out var h)) continue;
            if (h == r.Hash) duplicates++;
            else conflicts.Add(new { target = r.Target, sourceId = r.SourceId, concept = r.Fields.GetValueOrDefault("concept") ?? r.Fields.GetValueOrDefault("name") });
        }

        var accountsToCreate = new List<object>();
        var categoriesToCreate = new List<string>();
        if (parsed.Format == ImportParser.LegacyFormat)
        {
            var accounts = await db.Accounts.AsNoTracking().Where(a => a.OwnerId == owner && a.DeletedAt == null).Select(a => new { a.Name, a.Currency }).ToListAsync(ct);
            var categories = await db.Categories.AsNoTracking().Where(c => c.OwnerId == owner && c.DeletedAt == null).Select(c => c.Name).ToListAsync(ct);
            foreach (var g in parsed.Records.Where(r => r.Fields["account"] is not null).GroupBy(r => (name: r.Fields["account"]!, currency: r.Fields["currency"]!)))
            {
                if (!accounts.Any(a => string.Equals(a.Name, g.Key.name, StringComparison.OrdinalIgnoreCase) && a.Currency.ToString() == g.Key.currency))
                    accountsToCreate.Add(new { key = $"{g.Key.name}|{g.Key.currency}", name = g.Key.name, currency = g.Key.currency, usedByPlans = g.Any(r => r.Target == "plan") });
            }
            categoriesToCreate = parsed.Records.Select(r => r.Fields["category"]).Where(c => c is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(c => !categories.Contains(c!, StringComparer.OrdinalIgnoreCase)).Select(c => c!).ToList();
        }

        var summary = new Dictionary<string, object?>
        {
            ["format"] = parsed.Format,
            ["total"] = parsed.Records.Count,
            ["new"] = parsed.Records.Count - duplicates - conflicts.Count,
            ["duplicates"] = duplicates,
            ["conflicts"] = conflicts.Count,
            ["byTarget"] = parsed.Records.GroupBy(r => r.Target).ToDictionary(g => g.Key, g => g.Count()),
            ["byCurrency"] = parsed.Records.GroupBy(r => r.Fields.GetValueOrDefault("currency") ?? "-").ToDictionary(g => g.Key, g => g.Count()),
        };
        var now = clock.GetUtcNow();
        var batch = new ImportBatch
        {
            OwnerId = owner,
            FileHash = SHA256.HashData(bytes),
            Format = parsed.Format,
            FormatVersion = parsed.FormatVersion,
            Status = ImportStatus.Preview,
            NormalizedPayload = JsonSerializer.Serialize(parsed.Records, PayloadJson),
            SummaryJson = JsonSerializer.Serialize(summary, PayloadJson),
            CreatedAt = now,
            ExpiresAt = now + PreviewTtl,
        };
        db.ImportBatches.Add(batch);
        audit.Add(db, "import.preview", "success", "import", batch.Id);
        await db.SaveChangesAsync(ct);

        var sample = parsed.Records.Take(20).Select(r => new
        {
            target = r.Target,
            type = r.Fields.GetValueOrDefault("type") ?? r.Fields.GetValueOrDefault("kind"),
            concept = r.Fields.GetValueOrDefault("concept") ?? r.Fields.GetValueOrDefault("name"),
            amountMinor = r.Fields.GetValueOrDefault("amountMinor") ?? r.Fields.GetValueOrDefault("totalMinor"),
            currency = r.Fields.GetValueOrDefault("currency"),
            date = r.Fields.GetValueOrDefault("date") ?? r.Fields.GetValueOrDefault("firstDueDate"),
            count = r.Fields.GetValueOrDefault("count"),
            category = r.Fields.GetValueOrDefault("category"),
            account = r.Fields.GetValueOrDefault("account"),
        });
        return TypedResults.Ok(new
        {
            batchId = batch.Id,
            expiresAt = batch.ExpiresAt,
            summary,
            conflicts = conflicts.Take(50),
            warnings = parsed.Warnings,
            accountsToCreate,
            categoriesToCreate,
            sample,
        });
    }

    private static async Task<IResult> Commit(Guid id, CommitRequest req, HttpContext ctx, AppDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var key = ctx.Request.Headers["Idempotency-Key"].ToString();
        var v = new Validation();
        if (req.Confirm != true) v.Add("confirm", "Confirmá la importación.");
        if (key.Length is < 8 or > 80 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) v.Add("idempotencyKey", "Falta un Idempotency-Key válido.");
        var kinds = new Dictionary<string, AccountKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, value) in req.AccountKinds ?? [])
        {
            if (k.Length > 70 || !ApiMaps.AccountKinds.TryGetValue(value, out var kind)) { v.Add("accountKinds", "Tipo de cuenta no válido."); break; }
            kinds[k] = kind;
        }
        if (!v.IsValid) return v.Problem();

        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == id && b.OwnerId == owner, ct);
        if (batch is null) return Problems.NotFound();
        if (batch.Status == ImportStatus.Committed)
        {
            return batch.IdempotencyKey == key
                ? TypedResults.Ok(new { batchId = batch.Id, status = "committed", result = JsonDocument.Parse(batch.SummaryJson).RootElement })
                : Problems.Conflict("Esta importación ya fue confirmada.");
        }
        var now = clock.GetUtcNow();
        if (batch.Status != ImportStatus.Preview || batch.ExpiresAt < now || batch.NormalizedPayload is null)
            return Problems.Conflict("La vista previa caducó. Volvé a subir el archivo.");

        var records = JsonSerializer.Deserialize<List<NormalizedRecord>>(batch.NormalizedPayload, PayloadJson)!;
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IResult>(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var importer = new Importer(db, owner, batch.Id, now);
            ImportResult result;
            try
            {
                result = batch.Format == ImportParser.LegacyFormat
                    ? await importer.LegacyAsync(records, kinds, ct)
                    : await importer.ExportAsync(records, ct);
            }
            catch (Exception ex) when (ex is DbUpdateException || Db.IsRetryableConflict(ex))
            {
                // Los guardados intermedios de la restauración también son atómicos: nada queda escrito. Un 40001 en una
                // lectura de la transacción Serializable llega como PostgresException, no como DbUpdateException.
                await Db.RollbackQuietlyAsync(tx);
                db.ChangeTracker.Clear();
                return SaveFailure(ex);
            }
            if (result.Error is not null)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return result.Invalid ? Problems.Unprocessable(result.Error) : Problems.Conflict(result.Error);
            }
            batch.Status = ImportStatus.Committed;
            batch.IdempotencyKey = key;
            batch.CommittedAt = now;
            batch.NormalizedPayload = null;
            batch.SummaryJson = JsonSerializer.Serialize(result.Summary, PayloadJson);
            audit.Add(db, "import.commit", "success", "import", batch.Id);
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is DbUpdateException || Db.IsRetryableConflict(ex))
            {
                // Un 40001 en el COMMIT («pivot» de una dependencia con otra escritura Serializable) no es DbUpdateException.
                await Db.RollbackQuietlyAsync(tx);
                db.ChangeTracker.Clear();
                return SaveFailure(ex);
            }
            return TypedResults.Ok(new { batchId = batch.Id, status = "committed", result = result.Summary });
        });
    }

    /// <summary>
    /// Choque con otra escritura simultánea (al leer, al guardar o en el COMMIT) → 409; dato que la base rechaza (regla
    /// del archivo) → 422. Nunca 500. La vista previa sigue vigente: se puede reintentar.
    /// </summary>
    private static IResult SaveFailure(Exception ex) =>
        Db.IsRetryableConflict(ex)
            ? Problems.Conflict("Otra importación se guardó al mismo tiempo. Generá una vista previa nueva.")
            : Problems.Unprocessable("El archivo tiene datos que no cumplen las reglas de la aplicación. No se importó nada.");

    /// <summary>Invalid: el problema es del contenido del archivo (422); si no, es un conflicto con datos existentes (409).</summary>
    private sealed record ImportResult(Dictionary<string, int> Summary, string? Error, bool Invalid = false);

    private sealed class Importer(AppDbContext db, Guid owner, Guid batchId, DateTimeOffset now)
    {
        private readonly Dictionary<string, Guid> _map = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _summary = new() { ["created"] = 0, ["duplicates"] = 0, ["conflicts"] = 0 };
        private readonly HashSet<Guid> _createdPlans = [];

        private const string InTrash = "Un pago del archivo corresponde a un plan o gasto que está en la papelera. Restauralo y volvé a intentar.";

        private async Task<Dictionary<(string, string), Domain.ImportedRecord>> ExistingAsync(List<NormalizedRecord> records, CancellationToken ct)
        {
            var ids = records.Select(r => r.SourceId).Distinct().ToList();
            var list = await db.ImportedRecords.Where(r => r.OwnerId == owner && ids.Contains(r.SourceId)).ToListAsync(ct);
            return list.ToDictionary(r => (r.SourceType, r.SourceId));
        }

        private void Track(NormalizedRecord r, string targetType, Guid targetId)
        {
            db.ImportedRecords.Add(new Domain.ImportedRecord
            {
                OwnerId = owner, SourceType = r.SourceType, SourceId = r.SourceId, ContentHash = Convert.FromHexString(r.Hash),
                TargetType = targetType, TargetId = targetId, BatchId = batchId, CreatedAt = now,
            });
            _map[r.SourceType + ":" + r.SourceId] = targetId;
            _summary["created"]++;
        }

        public async Task<ImportResult> LegacyAsync(List<NormalizedRecord> records, Dictionary<string, AccountKind> kinds, CancellationToken ct)
        {
            var existing = await ExistingAsync(records, ct);
            var accounts = await db.Accounts.Where(a => a.OwnerId == owner && a.DeletedAt == null).ToListAsync(ct);
            var categories = await db.Categories.Where(c => c.OwnerId == owner && c.DeletedAt == null).ToListAsync(ct);
            foreach (var r in records)
            {
                if (existing.TryGetValue((r.SourceType, r.SourceId), out var ex))
                {
                    // Misma carga → no duplica. Contenido cambiado → conflicto visible, sin sobrescribir.
                    _summary[Convert.ToHexString(ex.ContentHash) == r.Hash ? "duplicates" : "conflicts"]++;
                    continue;
                }
                var f = r.Fields;
                var currency = Enum.Parse<Currency>(f["currency"]!);
                Account? account = null;
                if (f["account"] is { } accName)
                {
                    account = accounts.FirstOrDefault(a => string.Equals(a.Name, accName, StringComparison.OrdinalIgnoreCase) && a.Currency == currency);
                    if (account is null)
                    {
                        // Cuentas homónimas con monedas distintas se separan.
                        account = new Account
                        {
                            OwnerId = owner, Name = accName, Currency = currency,
                            Kind = kinds.GetValueOrDefault($"{accName}|{currency}", AccountKind.Bank),
                        };
                        db.Accounts.Add(account);
                        accounts.Add(account);
                    }
                }
                Category? category = null;
                if (f["category"] is { } catName)
                {
                    category = categories.FirstOrDefault(c => string.Equals(c.Name, catName, StringComparison.OrdinalIgnoreCase));
                    if (category is null)
                    {
                        category = new Category { OwnerId = owner, Name = catName, Kind = CategoryKind.Any };
                        db.Categories.Add(category);
                        categories.Add(category);
                    }
                }
                if (r.Target == "entry")
                {
                    var kind = f["type"] == "income" ? EntryKind.Income : EntryKind.Expense;
                    if (kind == EntryKind.Income && account?.Kind == AccountKind.CreditCard) account = null;
                    var e = new Entry
                    {
                        OwnerId = owner, Kind = kind, Concept = f["concept"]!, AmountMinor = M(f["amountMinor"]), Currency = currency,
                        EffectiveDate = D(f["date"]), AccountId = account?.Id, CategoryId = category?.Id,
                    };
                    db.Entries.Add(e);
                    Track(r, "entry", e.Id);
                }
                else
                {
                    // «amount» del prototipo es el TOTAL. Cuotas previas a hoy NO se marcan pagadas.
                    var plan = new InstallmentPlan
                    {
                        OwnerId = owner, Kind = f["type"] == "loan" ? PlanKind.Loan : PlanKind.Card, Concept = f["concept"]!,
                        TotalMinor = M(f["amountMinor"]), Count = L(f["count"]), Currency = currency, FirstDueDate = D(f["date"]),
                        AccountId = account?.Id, CategoryId = category?.Id,
                    };
                    plan.Installments = PlanSchedule.Build(plan);
                    db.Plans.Add(plan);
                    Track(r, "plan", plan.Id);
                }
            }
            return new ImportResult(_summary, null);
        }

        private Guid? Ref(string sourceTypeSuffix, string? sourceId) =>
            sourceId is null ? null : _map.TryGetValue("export_v2_" + sourceTypeSuffix + ":" + sourceId, out var g) ? g : null;

        private ImportResult Invalid(string error) => new(_summary, error, Invalid: true);

        public async Task<ImportResult> ExportAsync(List<NormalizedRecord> records, CancellationToken ct)
        {
            // Defensa en profundidad: las mismas reglas entre registros que la vista previa, antes de escribir nada.
            var inconsistent = ExportConsistency.Check(records);
            if (inconsistent.Count > 0) return Invalid("El archivo no es coherente: " + inconsistent[0]);
            var existing = await ExistingAsync(records, ct);
            foreach (var r in records)
            {
                if (!existing.TryGetValue((r.SourceType, r.SourceId), out var ex)) continue;
                if (Convert.ToHexString(ex.ContentHash) != r.Hash)
                    return new ImportResult(_summary, "Hay registros que ya existen con contenido distinto. La restauración no sobrescribe datos: revisá la vista previa.");
                _map[r.SourceType + ":" + r.SourceId] = ex.TargetId;
                _summary["duplicates"]++;
            }
            bool IsNew(NormalizedRecord r) => !existing.ContainsKey((r.SourceType, r.SourceId));
            DateTimeOffset? Deleted(Dictionary<string, string?> f) => f.GetValueOrDefault("deleted") == "true" ? now : null;

            foreach (var r in records.Where(r => r.Target == "account" && IsNew(r)))
            {
                var f = r.Fields;
                var a = new Account
                {
                    OwnerId = owner, Name = f["name"]!, Kind = ApiMaps.AccountKinds[f["kind"]!], Currency = Enum.Parse<Currency>(f["currency"]!),
                    Last4 = f["last4"], OpeningBalanceMinor = f["openingBalanceMinor"] is { } ob && SignedMinor.TryParse(ob, out var obv) ? obv : null,
                    OpeningDate = f["openingDate"] is { } od ? D(od) : null, DeletedAt = Deleted(f),
                };
                if (a.DeletedAt is null && await db.Accounts.AnyAsync(x => x.OwnerId == owner && x.DeletedAt == null && x.Name == a.Name && x.Currency == a.Currency, ct))
                    a.Name = Truncate(a.Name + " (restaurada)", 60);
                db.Accounts.Add(a);
                Track(r, "account", a.Id);
            }
            foreach (var r in records.Where(r => r.Target == "category" && IsNew(r)))
            {
                var f = r.Fields;
                var c = new Category { OwnerId = owner, Name = f["name"]!, Kind = ApiMaps.CategoryKinds[f["kind"]!], DeletedAt = Deleted(f) };
                if (c.DeletedAt is null && await db.Categories.AnyAsync(x => x.OwnerId == owner && x.DeletedAt == null && x.Name == c.Name, ct))
                {
                    // Categoría homónima existente: se reutiliza en vez de duplicarla.
                    var same = await db.Categories.FirstAsync(x => x.OwnerId == owner && x.DeletedAt == null && x.Name == c.Name, ct);
                    _map[r.SourceType + ":" + r.SourceId] = same.Id;
                    continue;
                }
                db.Categories.Add(c);
                Track(r, "category", c.Id);
            }
            await db.SaveChangesAsync(ct);

            foreach (var r in records.Where(r => r.Target == "entry" && IsNew(r)))
            {
                var f = r.Fields;
                var e = new Entry
                {
                    OwnerId = owner, Kind = ApiMaps.EntryKinds[f["kind"]!], Concept = f["concept"]!, Note = f["note"], AmountMinor = M(f["amountMinor"]),
                    Currency = Enum.Parse<Currency>(f["currency"]!), EffectiveDate = D(f["date"]),
                    AccountId = Ref("account", f["accountId"]), CategoryId = Ref("category", f["categoryId"]), DeletedAt = Deleted(f),
                };
                if (f["accountId"] is not null && e.AccountId is null || f["categoryId"] is not null && e.CategoryId is null)
                    return Invalid("Un movimiento referencia una cuenta o categoría que no está en el archivo.");
                db.Entries.Add(e);
                Track(r, "entry", e.Id);
            }
            foreach (var r in records.Where(r => r.Target == "plan" && IsNew(r)))
            {
                var f = r.Fields;
                var plan = new InstallmentPlan
                {
                    OwnerId = owner, Kind = ApiMaps.PlanKinds[f["kind"]!], Concept = f["concept"]!, Note = f["note"], TotalMinor = M(f["totalMinor"]),
                    Count = L(f["count"]), Currency = Enum.Parse<Currency>(f["currency"]!), FirstDueDate = D(f["firstDueDate"]),
                    AccountId = Ref("account", f["accountId"]), CategoryId = Ref("category", f["categoryId"]),
                    PaidBeforeCount = f["paidBeforeCount"] is { } pb ? L(pb) : 0, RemainingOnly = f["remainingOnly"] == "true",
                    DisbursementMinor = f["disbursementMinor"] is { } dm ? M(dm) : null, DisbursementDate = f["disbursementDate"] is { } dd ? D(dd) : null,
                    DisbursementAccountId = Ref("account", f["disbursementAccountId"]), DeletedAt = Deleted(f),
                };
                if (plan.PaidBeforeCount >= plan.Count) return Invalid("Un plan tiene más cuotas pagadas que cuotas.");
                plan.Installments = PlanSchedule.Build(plan);
                var amounts = f["installmentAmounts"]!.Split(',').Select(M).ToArray();
                for (var i = 0; i < amounts.Length; i++) plan.Installments[i].AmountMinor = amounts[i];
                db.Plans.Add(plan);
                Track(r, "plan", plan.Id);
                _createdPlans.Add(plan.Id);
            }
            foreach (var r in records.Where(r => r.Target == "transfer" && IsNew(r)))
            {
                var f = r.Fields;
                var from = Ref("account", f["fromAccountId"]);
                var to = Ref("account", f["toAccountId"]);
                if (from is null || to is null || from == to) return Invalid("Una transferencia referencia cuentas inválidas.");
                var t = new Transfer
                {
                    OwnerId = owner, FromAccountId = from.Value, ToAccountId = to.Value, Currency = Enum.Parse<Currency>(f["currency"]!),
                    AmountMinor = M(f["amountMinor"]), Date = D(f["date"]), Note = f["note"], DeletedAt = Deleted(f),
                };
                db.Transfers.Add(t);
                Track(r, "transfer", t.Id);
            }
            await db.SaveChangesAsync(ct);

            foreach (var r in records.Where(r => r.Target == "payment" && IsNew(r)))
            {
                var f = r.Fields;
                var source = Ref("account", f["sourceAccountId"]);
                if (source is null) return Invalid("Un pago referencia una cuenta que no está en el archivo.");
                var p = new Payment
                {
                    OwnerId = owner, SourceAccountId = source.Value, Date = D(f["date"]), Currency = Enum.Parse<Currency>(f["currency"]!),
                    AmountMinor = M(f["amountMinor"]), Note = f["note"], IdempotencyKey = "import-" + r.SourceId[..Math.Min(r.SourceId.Length, 60)],
                    RequestHash = Convert.FromHexString(r.Hash),
                };
                long sum = 0;
                foreach (var part in f["allocations"]!.Split('|'))
                {
                    var bits = part.Split(':');
                    var amount = M(bits[^1]);
                    sum = checked(sum + amount);
                    if (bits[0] == "e")
                    {
                        var entryId = Ref("entry", bits[1]);
                        var entry = entryId is null ? null : await db.Entries.Include(e => e.Account).SingleOrDefaultAsync(e => e.Id == entryId && e.OwnerId == owner, ct);
                        if (entry is null || entry.Account?.Kind != AccountKind.CreditCard || entry.AmountMinor - entry.PaidMinor < amount)
                            return Invalid("Un pago no coincide con los gastos del archivo.");
                        // Ya existía y se eliminó después de exportar: un pago vigente no puede quedar sobre algo en la papelera.
                        if (entry.DeletedAt is not null) return new ImportResult(_summary, InTrash);
                        entry.PaidMinor += amount;
                        p.Allocations.Add(new PaymentAllocation { OwnerId = owner, PaymentId = p.Id, EntryId = entry.Id, AmountMinor = amount });
                    }
                    else
                    {
                        var planId = Ref("plan", bits[1]);
                        var number = L(bits[2]);
                        var inst = planId is null ? null : await db.Installments.Include(i => i.Plan)
                            .SingleOrDefaultAsync(i => i.PlanId == planId && i.Number == number && i.OwnerId == owner, ct);
                        if (inst is null || inst.PendingMinor < amount) return Invalid("Un pago no coincide con las cuotas del archivo.");
                        if (inst.Plan!.DeletedAt is not null) return new ImportResult(_summary, InTrash);
                        inst.PaidMinor += amount;
                        // Plan que ya existía: igual que POST /payments, el pago escribe la fila del plan para chocar con un
                        // borrado o una edición simultánea (409) en vez de dejar un plan eliminado con pagos vigentes.
                        if (!_createdPlans.Contains(inst.PlanId)) CashEndpoints.TouchPlan(db, inst.Plan);
                        p.Allocations.Add(new PaymentAllocation
                        {
                            OwnerId = owner, PaymentId = p.Id, InstallmentId = inst.Id, PlanId = inst.PlanId, InstallmentNumber = inst.Number, AmountMinor = amount,
                        });
                    }
                }
                if (sum != p.AmountMinor) return Invalid("Un pago no suma lo mismo que sus asignaciones.");
                db.Payments.Add(p);
                Track(r, "payment", p.Id);
            }
            return new ImportResult(_summary, null);
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    }

    /// <summary>Exportación completa versionada. Descarga autenticada, no cacheable. Sin hashes, secretos MFA ni sesiones.</summary>
    private static async Task<IResult> Export(HttpContext ctx, AppDbContext db, AttemptLimiter limiter, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var owner = ctx.User.UserId();
        var limit = limiter.Bulk(owner);
        if (!limit.IsAllowed) return Problems.TooMany(limit.RetryAfter, ctx);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.OwnerId == owner).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.OwnerId == owner).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var entries = await db.Entries.AsNoTracking().Where(e => e.OwnerId == owner).OrderBy(e => e.CreatedAt).ToListAsync(ct);
        var plans = await db.Plans.AsNoTracking().Include(p => p.Installments).Where(p => p.OwnerId == owner).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var payments = await db.Payments.AsNoTracking().Include(p => p.Allocations).ThenInclude(a => a.Installment)
            .Where(p => p.OwnerId == owner && p.DeletedAt == null).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var transfers = await db.Transfers.AsNoTracking().Where(t => t.OwnerId == owner).OrderBy(t => t.CreatedAt).ToListAsync(ct);
        var now = clock.GetUtcNow();

        var doc = new
        {
            format = ImportParser.ExportFormat,
            version = 2,
            exportedAt = now,
            accounts = accounts.Select(a => new
            {
                id = a.Id, name = a.Name, kind = a.Kind.Api(), currency = a.Currency.ToString(), last4 = a.Last4,
                openingBalanceMinor = a.OpeningBalanceMinor?.ToString(CultureInfo.InvariantCulture), openingDate = a.OpeningDate.Api(), deleted = a.DeletedAt is not null,
            }),
            categories = categories.Select(c => new { id = c.Id, name = c.Name, kind = c.Kind.Api(), deleted = c.DeletedAt is not null }),
            entries = entries.Select(e => new
            {
                id = e.Id, kind = e.Kind.Api(), concept = e.Concept, note = e.Note, amountMinor = Money.ToApi(e.AmountMinor), currency = e.Currency.ToString(),
                date = e.EffectiveDate.Api(), accountId = e.AccountId, categoryId = e.CategoryId, deleted = e.DeletedAt is not null,
            }),
            plans = plans.Select(p => new
            {
                id = p.Id, kind = p.Kind.Api(), concept = p.Concept, note = p.Note, totalMinor = Money.ToApi(p.TotalMinor), count = p.Count,
                currency = p.Currency.ToString(), firstDueDate = p.FirstDueDate.Api(), accountId = p.AccountId, categoryId = p.CategoryId,
                paidBeforeCount = p.PaidBeforeCount, remainingOnly = p.RemainingOnly,
                disbursementMinor = p.DisbursementMinor is { } d ? Money.ToApi(d) : null, disbursementDate = p.DisbursementDate.Api(),
                disbursementAccountId = p.DisbursementAccountId,
                installments = p.Installments.OrderBy(i => i.Number).Select(i => new
                {
                    number = i.Number, amountMinor = Money.ToApi(i.AmountMinor), dueDate = i.DueDate.Api(), paidMinor = i.PaidMinor.ToString(CultureInfo.InvariantCulture), declaredPaid = i.DeclaredPaid,
                }),
                deleted = p.DeletedAt is not null,
            }),
            payments = payments.Select(p => new
            {
                id = p.Id, sourceAccountId = p.SourceAccountId, date = p.Date.Api(), currency = p.Currency.ToString(), amountMinor = Money.ToApi(p.AmountMinor), note = p.Note,
                allocations = p.Allocations.Select(a => new
                {
                    planId = a.Installment?.PlanId, installmentNumber = a.Installment?.Number, entryId = a.EntryId, amountMinor = Money.ToApi(a.AmountMinor),
                }),
            }),
            transfers = transfers.Select(t => new
            {
                id = t.Id, fromAccountId = t.FromAccountId, toAccountId = t.ToAccountId, currency = t.Currency.ToString(), amountMinor = Money.ToApi(t.AmountMinor),
                date = t.Date.Api(), note = t.Note, deleted = t.DeletedAt is not null,
            }),
        };
        await audit.WriteAsync(db, "export.create", "success");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        ctx.Response.Headers.CacheControl = "no-store";
        var stamp = FinanceCalendar.Today(clock, FinanceCalendar.ArgentinaZone).Api();
        return TypedResults.File(bytes, "application/json", $"alvarez-finanzas-export-{stamp}.json");
    }
}
