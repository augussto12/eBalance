using AlvarezFinanzas.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlvarezFinanzas.Infrastructure;

/// <param name="owner">
/// Propietario para los filtros globales por owner_id. Sin él (CLI, migraciones, procesos en segundo plano) cualquier
/// consulta de datos financieros falla, salvo que use explícitamente <c>IgnoreQueryFilters([OwnerFilter])</c>.
/// </param>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, TimeProvider clock, ICurrentOwner? owner = null)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    /// <summary>
    /// Nombre del filtro global por propietario: defensa adicional, no la única autorización (cada consulta sigue filtrando
    /// por el owner de la sesión). Ignorarlo solo en procesos operativos que recorren todos los propietarios.
    /// </summary>
    public const string OwnerFilter = "owner";

    /// <summary>Lo evalúa EF en cada consulta (no queda fijo en el modelo cacheado).</summary>
    private Guid CurrentOwnerId => owner?.OwnerId ?? throw new InvalidOperationException(
        "Consulta de datos financieros sin un propietario con sesión completa. Los procesos operativos que recorren " +
        "todos los propietarios deben usar IgnoreQueryFilters([AppDbContext.OwnerFilter]) de forma explícita.");

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<InstallmentPlan> Plans => Set<InstallmentPlan>();
    public DbSet<Installment> Installments => Set<Installment>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportedRecord> ImportedRecords => Set<ImportedRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<KeyRingSentinel> KeyRingSentinels => Set<KeyRingSentinel>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(60);
            e.Property(u => u.TimeZoneId).HasMaxLength(64);
            e.Property(u => u.TotpSecretProtected).HasMaxLength(1000);
            e.Property(u => u.TotpPendingSecretProtected).HasMaxLength(1000);
            e.Property(u => u.ProfileVersion).IsConcurrencyToken();
        });

        b.Entity<Account>(e =>
        {
            Owned(e);
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.Last4).HasMaxLength(4).IsFixedLength();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            e.HasIndex(x => new { x.OwnerId, x.Name, x.Currency }).IsUnique().HasFilter("deleted_at IS NULL");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_accounts_last4", "last4 IS NULL OR last4 ~ '^[0-9]{4}$'");
                t.HasCheckConstraint("ck_accounts_currency", "currency IN ('ARS','USD')");
                t.HasCheckConstraint("ck_accounts_opening", "(opening_balance_minor IS NULL) = (opening_date IS NULL)");
            });
        });

        b.Entity<Category>(e =>
        {
            Owned(e);
            e.Property(x => x.Name).HasMaxLength(40);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.OwnerId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
        });

        b.Entity<Entry>(e =>
        {
            Owned(e);
            e.Property(x => x.Concept).HasMaxLength(90);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            OwnedRef(e.HasOne(x => x.Account).WithMany(), x => new { x.OwnerId, x.AccountId });
            OwnedRef(e.HasOne(x => x.Category).WithMany(), x => new { x.OwnerId, x.CategoryId });
            e.HasIndex(x => new { x.OwnerId, x.Currency, x.EffectiveDate });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_entries_amount", $"amount_minor > 0 AND amount_minor <= {Money.MaxAmountMinor}");
                t.HasCheckConstraint("ck_entries_paid", "paid_minor >= 0 AND paid_minor <= amount_minor");
                t.HasCheckConstraint("ck_entries_currency", "currency IN ('ARS','USD')");
            });
        });

        b.Entity<InstallmentPlan>(e =>
        {
            Owned(e);
            e.ToTable("installment_plans", t =>
            {
                t.HasCheckConstraint("ck_plans_total", $"total_minor > 0 AND total_minor <= {Money.MaxAmountMinor}");
                t.HasCheckConstraint("ck_plans_count", $"count >= 1 AND count <= {Money.MaxInstallments} AND count <= total_minor");
                t.HasCheckConstraint("ck_plans_paid_before", "paid_before_count >= 0 AND paid_before_count < count");
                t.HasCheckConstraint("ck_plans_currency", "currency IN ('ARS','USD')");
                t.HasCheckConstraint("ck_plans_disbursement", "disbursement_minor IS NULL OR (disbursement_minor > 0 AND disbursement_date IS NOT NULL AND kind = 'Loan')");
            });
            e.Property(x => x.Concept).HasMaxLength(90);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            OwnedRef(e.HasOne(x => x.Account).WithMany(), x => new { x.OwnerId, x.AccountId });
            OwnedRef(e.HasOne(x => x.Category).WithMany(), x => new { x.OwnerId, x.CategoryId });
            OwnedRef(e.HasOne(x => x.DisbursementAccount).WithMany(), x => new { x.OwnerId, x.DisbursementAccountId });
            e.HasIndex(x => new { x.OwnerId, x.Currency, x.FirstDueDate });
        });

        b.Entity<Installment>(e =>
        {
            e.HasQueryFilter(OwnerFilter, x => x.OwnerId == CurrentOwnerId);
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.OwnerId, x.Id });
            e.Property(x => x.Version).IsConcurrencyToken();
            RollbackSafeStamps(e);
            e.HasOne(x => x.Plan).WithMany(p => p.Installments)
                .HasForeignKey(x => new { x.OwnerId, x.PlanId })
                .HasPrincipalKey(p => new { p.OwnerId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PlanId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.DueDate });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_installments_amount", "amount_minor > 0");
                t.HasCheckConstraint("ck_installments_number", "number >= 1");
                t.HasCheckConstraint("ck_installments_paid", "paid_minor >= 0 AND paid_minor <= amount_minor");
            });
        });

        b.Entity<Payment>(e =>
        {
            Owned(e);
            e.Property(x => x.Note).HasMaxLength(200);
            e.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            e.Property(x => x.IdempotencyKey).HasMaxLength(80);
            OwnedRef(e.HasOne(x => x.SourceAccount).WithMany(), x => new { x.OwnerId, x.SourceAccountId });
            e.HasIndex(x => new { x.OwnerId, x.IdempotencyKey }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_payments_amount", $"amount_minor > 0 AND amount_minor <= {Money.MaxAmountMinor}"));
        });

        b.Entity<PaymentAllocation>(e =>
        {
            e.HasQueryFilter(OwnerFilter, x => x.OwnerId == CurrentOwnerId);
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);
            RollbackSafeStamps(e);
            e.HasOne(x => x.Payment).WithMany(p => p.Allocations)
                .HasForeignKey(x => new { x.OwnerId, x.PaymentId })
                .HasPrincipalKey(p => new { p.OwnerId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Installment).WithMany()
                .HasForeignKey(x => new { x.OwnerId, x.InstallmentId })
                .HasPrincipalKey(p => new { p.OwnerId, p.Id })
                .OnDelete(DeleteBehavior.Restrict);
            OwnedRef(e.HasOne(x => x.Entry).WithMany(), x => new { x.OwnerId, x.EntryId });
            // Los planes solo se eliminan de forma reversible: la copia del plan siempre resuelve.
            OwnedRef(e.HasOne(x => x.Plan).WithMany(), x => new { x.OwnerId, x.PlanId });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_allocations_amount", "amount_minor > 0");
                t.HasCheckConstraint("ck_allocations_installment_number", "installment_number IS NULL OR installment_number >= 1");
                // Una cuota o un gasto, nunca ambos; un gasto no lleva copia de plan; sin cuota ni gasto (pago revertido cuya
                // cuota dejó de existir) queda la copia de plan y número. Una cuota sin copia sigue siendo válida: así inserta
                // la versión anterior (rollback sin revertir migraciones).
                t.HasCheckConstraint("ck_allocations_target",
                    "NOT (installment_id IS NOT NULL AND entry_id IS NOT NULL)"
                    + " AND (entry_id IS NULL OR (plan_id IS NULL AND installment_number IS NULL))"
                    + " AND (installment_id IS NOT NULL OR entry_id IS NOT NULL OR (plan_id IS NOT NULL AND installment_number IS NOT NULL))");
            });
        });

        b.Entity<Transfer>(e =>
        {
            Owned(e);
            e.Property(x => x.Note).HasMaxLength(200);
            e.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
            OwnedRef(e.HasOne(x => x.FromAccount).WithMany(), x => new { x.OwnerId, x.FromAccountId });
            OwnedRef(e.HasOne(x => x.ToAccount).WithMany(), x => new { x.OwnerId, x.ToAccountId });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_transfers_amount", $"amount_minor > 0 AND amount_minor <= {Money.MaxAmountMinor}");
                t.HasCheckConstraint("ck_transfers_distinct", "from_account_id <> to_account_id");
            });
        });

        b.Entity<ImportBatch>(e =>
        {
            e.HasQueryFilter(OwnerFilter, x => x.OwnerId == CurrentOwnerId);
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Format).HasMaxLength(32);
            e.Property(x => x.IdempotencyKey).HasMaxLength(80);
            e.Property(x => x.NormalizedPayload).HasColumnType("jsonb");
            e.Property(x => x.SummaryJson).HasColumnType("jsonb");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        });

        b.Entity<ImportedRecord>(e =>
        {
            e.HasQueryFilter(OwnerFilter, x => x.OwnerId == CurrentOwnerId);
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceType).HasMaxLength(32);
            e.Property(x => x.SourceId).HasMaxLength(100);
            e.Property(x => x.TargetType).HasMaxLength(32);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.SourceType, x.SourceId }).IsUnique();
        });

        b.Entity<AuditEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.ResourceType).HasMaxLength(32);
            e.Property(x => x.ResourceId).HasMaxLength(64);
            e.Property(x => x.Result).HasMaxLength(32);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.IpPrefix).HasMaxLength(64);
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.ActorId, x.OccurredAt });
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RevokedReason).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(160);
            e.Property(x => x.IpPrefix).HasMaxLength(64);
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.RevokedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecoveryCode>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.CodeHash }).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<KeyRingSentinel>(e =>
        {
            e.ToTable("key_ring_sentinel", t => t.HasCheckConstraint("ck_key_ring_sentinel_single", "id = 1"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ProtectedValue).HasMaxLength(1000);
        });
    }

    private void Owned<T>(EntityTypeBuilder<T> e) where T : OwnedEntity
    {
        e.HasQueryFilter(OwnerFilter, x => x.OwnerId == CurrentOwnerId);
        e.HasKey(x => x.Id);
        e.HasAlternateKey(x => new { x.OwnerId, x.Id });
        e.Property(x => x.Version).IsConcurrencyToken();
        e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.OwnerId, x.DeletedAt });
    }

    /// <summary>
    /// Columnas agregadas después de la primera versión: DEFAULT en la base solo como red para que la versión anterior
    /// (rollback sin revertir migraciones, expand/contract) pueda seguir insertando. La app siempre las sella en SaveChangesAsync.
    /// </summary>
    private static void RollbackSafeStamps<T>(EntityTypeBuilder<T> e) where T : class, IStampedRecord
    {
        e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
    }

    /// <summary>FK compuesta (owner_id, id): impide relacionar registros de otro propietario a nivel de base.</summary>
    private static void OwnedRef<TDep, TPrin>(ReferenceCollectionBuilder<TPrin, TDep> rel,
        System.Linq.Expressions.Expression<Func<TDep, object?>> fk)
        where TDep : class where TPrin : OwnedEntity
    {
        rel.HasForeignKey(fk).HasPrincipalKey(p => new { p.OwnerId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>Sella fechas (UTC) y versión de todo registro financiero (<see cref="IStampedRecord"/>): cuentas, movimientos, planes, cuotas, pagos, asignaciones y transferencias.</summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<IStampedRecord>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                entry.Entity.Version = 1;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Entity.Version++;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
