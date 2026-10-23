using Microsoft.AspNetCore.Identity;

namespace AlvarezFinanzas.Domain;

/// <summary>
/// Fechas de alta/modificación (UTC) y versión optimista. Las sella <c>AppDbContext.SaveChangesAsync</c>: alta → versión 1,
/// modificación → versión + 1. Nunca vienen del cliente.
/// </summary>
public interface IStampedRecord
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
    long Version { get; set; }
}

/// <summary>Registro financiero perteneciente a un propietario. El propietario sale de la sesión, nunca del request.</summary>
public abstract class OwnedEntity : IStampedRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Token de concurrencia optimista; se incrementa en cada modificación.</summary>
    public long Version { get; set; } = 1;
    public DateTimeOffset? DeletedAt { get; set; }
}

public enum AccountKind { Cash, Bank, Wallet, CreditCard }

public sealed class Account : OwnedEntity
{
    public required string Name { get; set; }
    public AccountKind Kind { get; set; }
    public Currency Currency { get; set; }
    /// <summary>Últimos cuatro dígitos opcionales. Nunca el número completo.</summary>
    public string? Last4 { get; set; }
    /// <summary>Saldo inicial opcional; sin él no se calcula saldo de caja.</summary>
    public long? OpeningBalanceMinor { get; set; }
    public DateOnly? OpeningDate { get; set; }
}

public enum CategoryKind { Expense, Income, Any }

public sealed class Category : OwnedEntity
{
    public required string Name { get; set; }
    public CategoryKind Kind { get; set; }
}

public enum EntryKind { Income, Expense }

/// <summary>Ingreso o gasto simple, asignado presupuestariamente a su fecha.</summary>
public sealed class Entry : OwnedEntity
{
    public EntryKind Kind { get; set; }
    public required string Concept { get; set; }
    public string? Note { get; set; }
    public long AmountMinor { get; set; }
    public Currency Currency { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }
    /// <summary>Importe ya cancelado mediante pagos (solo gastos cargados a tarjeta de crédito).</summary>
    public long PaidMinor { get; set; }
}

public enum PlanKind { Card, Loan }

/// <summary>Compra en cuotas o plan de pagos de préstamo. Es un compromiso, no salida de caja inmediata.</summary>
public sealed class InstallmentPlan : OwnedEntity
{
    public PlanKind Kind { get; set; }
    public required string Concept { get; set; }
    public string? Note { get; set; }
    /// <summary>Total de TODAS las cuotas (incluye intereses en préstamos).</summary>
    public long TotalMinor { get; set; }
    public int Count { get; set; }
    public DateOnly FirstDueDate { get; set; }
    public Currency Currency { get; set; }
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }
    /// <summary>Cuotas que el propietario declaró pagadas antes de registrar el plan (crédito en curso).</summary>
    public int PaidBeforeCount { get; set; }
    /// <summary>El plan registra solo las cuotas restantes de un crédito ya en curso.</summary>
    public bool RemainingOnly { get; set; }
    /// <summary>Capital recibido (préstamos): ingreso de caja separado, no ingreso presupuestario.</summary>
    public long? DisbursementMinor { get; set; }
    public DateOnly? DisbursementDate { get; set; }
    public Guid? DisbursementAccountId { get; set; }
    public Account? DisbursementAccount { get; set; }
    public List<Installment> Installments { get; set; } = [];
}

public sealed class Installment : IStampedRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid PlanId { get; set; }
    public InstallmentPlan? Plan { get; set; }
    public int Number { get; set; }
    public long AmountMinor { get; set; }
    public DateOnly DueDate { get; set; }
    public long PaidMinor { get; set; }
    /// <summary>Declarada pagada fuera de la app al registrar un crédito en curso (sin pago asociado).</summary>
    public bool DeclaredPaid { get; set; }
    public long Version { get; set; } = 1;

    public long PendingMinor => DeclaredPaid ? 0 : AmountMinor - PaidMinor;
}

/// <summary>Pago a tarjeta/cuota: mueve caja y cancela obligaciones, sin crear un segundo gasto.</summary>
public sealed class Payment : OwnedEntity
{
    public Guid SourceAccountId { get; set; }
    public Account? SourceAccount { get; set; }
    public DateOnly Date { get; set; }
    public long AmountMinor { get; set; }
    public Currency Currency { get; set; }
    public string? Note { get; set; }
    public required string IdempotencyKey { get; set; }
    public required byte[] RequestHash { get; set; }
    public List<PaymentAllocation> Allocations { get; set; } = [];
}

public sealed class PaymentAllocation : IStampedRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
    public Guid PaymentId { get; set; }
    public Payment? Payment { get; set; }
    /// <summary>
    /// Cuota pagada. Queda en null solo en la asignación de un pago revertido cuya cuota dejó de existir al acortar el
    /// plan: el historial conserva el plan y el número (<see cref="PlanId"/>, <see cref="InstallmentNumber"/>).
    /// </summary>
    public Guid? InstallmentId { get; set; }
    public Installment? Installment { get; set; }
    /// <summary>Copia del plan y del número de la cuota al crear la asignación (null en los gastos de tarjeta).</summary>
    public Guid? PlanId { get; set; }
    public InstallmentPlan? Plan { get; set; }
    public int? InstallmentNumber { get; set; }
    public Guid? EntryId { get; set; }
    public Entry? Entry { get; set; }
    public long AmountMinor { get; set; }
}

/// <summary>Transferencia entre cuentas propias de la misma moneda: efecto neto cero.</summary>
public sealed class Transfer : OwnedEntity
{
    public Guid FromAccountId { get; set; }
    public Account? FromAccount { get; set; }
    public Guid ToAccountId { get; set; }
    public Account? ToAccount { get; set; }
    public Currency Currency { get; set; }
    public long AmountMinor { get; set; }
    public DateOnly Date { get; set; }
    public string? Note { get; set; }
}

public enum ImportStatus { Preview, Committed, Expired }

public sealed class ImportBatch
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public required byte[] FileHash { get; set; }
    public required string Format { get; set; }
    public int FormatVersion { get; set; }
    public ImportStatus Status { get; set; }
    /// <summary>Datos normalizados de la vista previa. Temporales: se borran al confirmar o caducar.</summary>
    public string? NormalizedPayload { get; set; }
    public required string SummaryJson { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CommittedAt { get; set; }
}

public sealed class ImportedRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public required byte[] ContentHash { get; set; }
    public required string TargetType { get; set; }
    public Guid TargetId { get; set; }
    public Guid BatchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Auditoría sin importes ni conceptos. Un administrador de la base puede alterarla: no es inviolable.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public Guid? ActorId { get; set; }
    public required string Action { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public required string Result { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? CorrelationId { get; set; }
    /// <summary>IP minimizada (/24 en IPv4, /48 en IPv6).</summary>
    public string? IpPrefix { get; set; }
}

/// <summary>
/// Centinela del key ring de Data Protection: un valor conocido protegido la primera vez que la API arranca con esta base.
/// Si al arrancar no se puede desproteger, el key ring se perdió o se reemplazó y la API no arranca (no se generan claves
/// nuevas en silencio). Fila única (id = 1).
/// </summary>
public sealed class KeyRingSentinel
{
    public int Id { get; set; } = 1;
    public required string ProtectedValue { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AppUser : IdentityUser<Guid>
{
    public AppUser() => Id = Guid.CreateVersion7();
    public string DisplayName { get; set; } = "";
    public string TimeZoneId { get; set; } = "America/Argentina/Buenos_Aires";
    public bool IsActive { get; set; } = true;
    /// <summary>Secreto TOTP protegido con Data Protection (clave fuera de la base).</summary>
    public string? TotpSecretProtected { get; set; }
    public string? TotpPendingSecretProtected { get; set; }
    public DateTimeOffset? TotpPendingCreatedAt { get; set; }
    /// <summary>Último timestep TOTP aceptado; impide reutilizar el mismo código.</summary>
    public long? TotpLastTimeStep { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long ProfileVersion { get; set; } = 1;
}

public sealed class RecoveryCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required byte[] CodeHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public enum SessionKind { Full, PreAuth }

public sealed class UserSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>SHA-256 de la referencia aleatoria (256 bits) que viaja protegida en la cookie.</summary>
    public required byte[] KeyHash { get; set; }
    public Guid UserId { get; set; }
    public SessionKind Kind { get; set; }
    public byte[]? ProtectedTicket { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? ElevatedUntil { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? UserAgent { get; set; }
    public string? IpPrefix { get; set; }
}
