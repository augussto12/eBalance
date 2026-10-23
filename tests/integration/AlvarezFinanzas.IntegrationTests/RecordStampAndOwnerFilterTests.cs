using System.Net;
using System.Text.Json;
using AlvarezFinanzas.Api;
using AlvarezFinanzas.Domain;
using AlvarezFinanzas.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AlvarezFinanzas.IntegrationTests;

internal static class RecordTestSupport
{
    /// <summary>PostgreSQL guarda microsegundos: el instante esperado se trunca igual.</summary>
    public static DateTimeOffset Micro(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % 10, TimeSpan.Zero);

    public static async Task<Guid> OwnerIdAsync(Client c) =>
        Guid.Parse((await c.GetJson("/api/v1/auth/me")).GetProperty("user").Str("id"));

    public static string LegacyImport(string tag) =>
        $$"""{"version":1,"records":[{"id":"r-{{tag}}","type":"income","name":"Importado {{tag}}","amount":1000,"currency":"ARS","date":"2026-01-05","category":"Trabajo","account":"Banco","count":1}]}""";
}

/// <summary>02-MOD-01: todo registro financiero —también cuotas y asignaciones de pago— tiene created_at, updated_at y version.</summary>
public class RecordStampTests(PostgresFixture pg)
{
    [Fact]
    public async Task Installments_and_allocations_get_utc_timestamps_and_versions_from_the_server()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync();
        var owner = await RecordTestSupport.OwnerIdAsync(a);
        var bank = await a.AccountAsync("Banco");
        var t0 = app.Clock.GetUtcNow();
        var plan = await a.PostJson("/api/v1/plans", new { kind = "card", concept = "Monitor", amountMode = "total", amountMinor = "30000", count = 3, currency = "ARS", firstDueDate = "2026-10-05" });
        var first = Guid.Parse(plan.GetProperty("installments")[0].Str("id"));
        await using (var db = app.NewDbContext(owner))
        {
            var all = await db.Installments.AsNoTracking().ToListAsync();
            Assert.Equal(3, all.Count);
            Assert.All(all, i =>
            {
                Assert.Equal(TimeSpan.Zero, i.CreatedAt.Offset);
                Assert.Equal(RecordTestSupport.Micro(t0), i.CreatedAt);
                Assert.Equal(i.CreatedAt, i.UpdatedAt);
                Assert.Equal(1, i.Version);
            });
        }

        app.Clock.Advance(TimeSpan.FromMinutes(10));
        var t1 = app.Clock.GetUtcNow();
        var pay = await a.PostJson("/api/v1/payments", new { sourceAccountId = bank, date = "2026-10-05", currency = "ARS", amountMinor = "4000",
            allocations = new[] { new { installmentId = first, amountMinor = "4000" } } }, HttpStatusCode.Created,
            new Dictionary<string, string> { ["Idempotency-Key"] = "pago-sellos-0001" });
        await using (var db = app.NewDbContext(owner))
        {
            var paid = await db.Installments.AsNoTracking().SingleAsync(i => i.Id == first);
            Assert.Equal(RecordTestSupport.Micro(t0), paid.CreatedAt);
            Assert.Equal(RecordTestSupport.Micro(t1), paid.UpdatedAt);
            Assert.Equal(2, paid.Version);
            var untouched = await db.Installments.AsNoTracking().SingleAsync(i => i.PlanId == paid.PlanId && i.Number == 2);
            Assert.Equal(1, untouched.Version);
            Assert.Equal(RecordTestSupport.Micro(t0), untouched.UpdatedAt);
            var allocation = await db.PaymentAllocations.AsNoTracking().SingleAsync();
            Assert.Equal(RecordTestSupport.Micro(t1), allocation.CreatedAt);
            Assert.Equal(allocation.CreatedAt, allocation.UpdatedAt);
            Assert.Equal(1, allocation.Version);
        }

        // Revertir el pago modifica la cuota otra vez; la asignación queda inmutable como historia del pago revertido.
        app.Clock.Advance(TimeSpan.FromMinutes(10));
        var t2 = app.Clock.GetUtcNow();
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync($"/api/v1/payments/{pay.Str("id")}?version=1")).StatusCode);
        await using (var db = app.NewDbContext(owner))
        {
            var reverted = await db.Installments.AsNoTracking().SingleAsync(i => i.Id == first);
            Assert.Equal(0, reverted.PaidMinor);
            Assert.Equal(3, reverted.Version);
            Assert.Equal(RecordTestSupport.Micro(t2), reverted.UpdatedAt);
            var allocation = await db.PaymentAllocations.AsNoTracking().SingleAsync();
            Assert.Equal(1, allocation.Version);
            Assert.Equal(RecordTestSupport.Micro(t1), allocation.UpdatedAt);
        }
    }

    [Fact]
    public async Task Migration_backfills_installment_and_allocation_stamps_from_their_parent_records()
    {
        var cs = await pg.CreateDatabaseAsync();
        await using (var db = new AppDbContext(TestApp.DbOptions(cs), TimeProvider.System))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261022194023_Initial");
        }
        var user = Guid.NewGuid();
        var account = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var (paidInstallment, openInstallment) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = Guid.NewGuid();
        var allocation = Guid.NewGuid();
        var planCreated = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var planUpdated = new DateTimeOffset(2026, 2, 1, 9, 30, 0, TimeSpan.Zero);
        var paymentCreated = new DateTimeOffset(2026, 3, 5, 15, 0, 0, TimeSpan.Zero);
        var paymentUpdated = new DateTimeOffset(2026, 3, 20, 18, 45, 0, TimeSpan.Zero);
        // Filas con el esquema anterior (SQL parametrizado, datos ficticios).
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "AspNetUsers" (id, display_name, time_zone_id, is_active, created_at, profile_version, user_name, normalized_user_name,
                    email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count)
                VALUES (@user, 'Prueba', 'America/Argentina/Buenos_Aires', true, @planCreated, 1, 'prueba', 'PRUEBA', false, false, false, true, 0);
                INSERT INTO accounts (id, name, kind, currency, owner_id, created_at, updated_at, version)
                VALUES (@account, 'Banco', 'Bank', 'ARS', @user, @planCreated, @planCreated, 1);
                INSERT INTO installment_plans (id, kind, concept, total_minor, count, first_due_date, currency, paid_before_count, remaining_only,
                    owner_id, created_at, updated_at, version)
                VALUES (@plan, 'Card', 'Monitor', 20000, 2, DATE '2026-01-10', 'ARS', 0, false, @user, @planCreated, @planUpdated, 2);
                INSERT INTO installments (id, owner_id, plan_id, number, amount_minor, due_date, paid_minor, declared_paid, version)
                VALUES (@paidInstallment, @user, @plan, 1, 10000, DATE '2026-01-10', 5000, false, 2),
                       (@openInstallment, @user, @plan, 2, 10000, DATE '2026-02-10', 0, false, 1);
                INSERT INTO payments (id, source_account_id, date, amount_minor, currency, idempotency_key, request_hash, owner_id, created_at, updated_at, version)
                VALUES (@payment, @account, DATE '2026-03-05', 5000, 'ARS', 'migracion-0001', @hash, @user, @paymentCreated, @paymentUpdated, 1);
                INSERT INTO payment_allocations (id, owner_id, payment_id, installment_id, amount_minor)
                VALUES (@allocation, @user, @payment, @paidInstallment, 5000);
                """, conn);
            cmd.Parameters.AddWithValue("user", user);
            cmd.Parameters.AddWithValue("account", account);
            cmd.Parameters.AddWithValue("plan", plan);
            cmd.Parameters.AddWithValue("paidInstallment", paidInstallment);
            cmd.Parameters.AddWithValue("openInstallment", openInstallment);
            cmd.Parameters.AddWithValue("payment", payment);
            cmd.Parameters.AddWithValue("allocation", allocation);
            cmd.Parameters.AddWithValue("planCreated", planCreated);
            cmd.Parameters.AddWithValue("planUpdated", planUpdated);
            cmd.Parameters.AddWithValue("paymentCreated", paymentCreated);
            cmd.Parameters.AddWithValue("paymentUpdated", paymentUpdated);
            cmd.Parameters.AddWithValue("hash", new byte[32]);
            await cmd.ExecuteNonQueryAsync();
        }

        // El comando operativo «migrate», como en restore.sh y restore-test.sh (sin GRANT: aquí no existen los roles de producción).
        var migrate = await CliRunner.RunAsync("migrate", cs, Path.GetTempPath(), environment: "Testing",
            extra: [$"--ConnectionStrings:Migrator={cs}", "--App:SkipGrants=true"]);
        Assert.True(migrate.Code == 0, migrate.Output);
        Assert.Contains("_RecordStampsAndKeyRingSentinel", migrate.Output);

        await using (var db = new AppDbContext(TestApp.DbOptions(cs), TimeProvider.System, new FixedOwner(user)))
        {
            var paid = await db.Installments.AsNoTracking().SingleAsync(i => i.Id == paidInstallment);
            Assert.Equal(planCreated, paid.CreatedAt);
            Assert.Equal(paymentUpdated, paid.UpdatedAt); // el pago (y su reversión) es posterior a la última edición del plan
            Assert.Equal(2, paid.Version);
            var open = await db.Installments.AsNoTracking().SingleAsync(i => i.Id == openInstallment);
            Assert.Equal(planCreated, open.CreatedAt);
            Assert.Equal(planUpdated, open.UpdatedAt);
            var alloc = await db.PaymentAllocations.AsNoTracking().SingleAsync();
            Assert.Equal(paymentCreated, alloc.CreatedAt);
            Assert.Equal(paymentCreated, alloc.UpdatedAt);
            Assert.Equal(1, alloc.Version);
        }
        // Expand compatible con un rollback: la versión anterior inserta cuotas y asignaciones sin conocer las columnas nuevas.
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            var (otherPlan, oldInstallment, oldPayment, oldAllocation) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO installment_plans (id, kind, concept, total_minor, count, first_due_date, currency, paid_before_count, remaining_only,
                    owner_id, created_at, updated_at, version)
                VALUES (@plan, 'Card', 'Versión anterior', 10000, 1, DATE '2026-04-10', 'ARS', 0, false, @user, now(), now(), 1);
                INSERT INTO installments (id, owner_id, plan_id, number, amount_minor, due_date, paid_minor, declared_paid, version)
                VALUES (@installment, @user, @plan, 1, 10000, DATE '2026-04-10', 10000, false, 2);
                INSERT INTO payments (id, source_account_id, date, amount_minor, currency, idempotency_key, request_hash, owner_id, created_at, updated_at, version)
                VALUES (@payment, @account, DATE '2026-04-10', 10000, 'ARS', 'rollback-0001', @hash, @user, now(), now(), 1);
                INSERT INTO payment_allocations (id, owner_id, payment_id, installment_id, amount_minor)
                VALUES (@allocation, @user, @payment, @installment, 10000);
                """, conn))
            {
                cmd.Parameters.AddWithValue("plan", otherPlan);
                cmd.Parameters.AddWithValue("installment", oldInstallment);
                cmd.Parameters.AddWithValue("payment", oldPayment);
                cmd.Parameters.AddWithValue("allocation", oldAllocation);
                cmd.Parameters.AddWithValue("user", user);
                cmd.Parameters.AddWithValue("account", account);
                cmd.Parameters.AddWithValue("hash", new byte[32]);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = new NpgsqlCommand("""
                SELECT a.version, a.created_at > now() - interval '1 minute', i.updated_at > now() - interval '1 minute'
                FROM payment_allocations a JOIN installments i ON i.id = a.installment_id WHERE a.id = @allocation
                """, conn))
            {
                cmd.Parameters.AddWithValue("allocation", oldAllocation);
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.True(reader.GetBoolean(1));
                Assert.True(reader.GetBoolean(2));
            }
        }
    }
}

/// <summary>
/// 02-MOD-17: filtros globales por owner_id como defensa adicional. Cada endpoint sigue filtrando explícitamente; el filtro
/// cubre un Where olvidado en lecturas, navegaciones y actualizaciones masivas. Sin propietario (CLI, segundo plano) los
/// datos financieros solo se ven con IgnoreQueryFilters explícito.
/// </summary>
public class OwnerQueryFilterTests(PostgresFixture pg)
{
    private static async Task SeedAsync(Client c, string tag)
    {
        var bank = await c.AccountAsync("Banco " + tag);
        var cash = await c.AccountAsync("Caja " + tag, "cash");
        await c.PostJson("/api/v1/categories", new { name = "Cat " + tag });
        await c.PostJson("/api/v1/entries", new { kind = "income", concept = "Sueldo " + tag, amountMinor = "500000", currency = "ARS", date = "2026-10-05", accountId = bank });
        var plan = await c.PostJson("/api/v1/plans", new { kind = "card", concept = "Plan " + tag, amountMode = "total", amountMinor = "60000", count = 3, currency = "ARS", firstDueDate = "2026-10-05" });
        await c.PostJson("/api/v1/payments", new { sourceAccountId = bank, date = "2026-10-06", currency = "ARS", amountMinor = "20000",
            allocations = new[] { new { installmentId = Guid.Parse(plan.GetProperty("installments")[0].Str("id")), amountMinor = "20000" } } },
            HttpStatusCode.Created, new Dictionary<string, string> { ["Idempotency-Key"] = $"filtro-{tag}-0001" });
        await c.PostJson("/api/v1/transfers", new { fromAccountId = bank, toAccountId = cash, currency = "ARS", amountMinor = "100", date = "2026-10-06" });
        var preview = await c.PostAsync("/api/v1/imports/preview", RecordTestSupport.LegacyImport(tag));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        await c.PostJson($"/api/v1/imports/{(await preview.Json()).Str("batchId")}/commit", new { confirm = true }, HttpStatusCode.OK,
            new Dictionary<string, string> { ["Idempotency-Key"] = $"filtro-import-{tag}" });
    }

    private static async Task AssertOnlyOwnerAsync<T>(IQueryable<T> query, Func<T, Guid> owner, Guid expected)
    {
        var rows = await query.ToListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(expected, owner(r)));
    }

    [Fact]
    public async Task Global_owner_filter_limits_reads_navigations_and_bulk_updates_even_without_an_explicit_where()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync("augusto");
        var b = await app.SignedInAsync("intruso", "otra frase larga para el intruso");
        await SeedAsync(a, "A");
        await SeedAsync(b, "B");
        var idA = await RecordTestSupport.OwnerIdAsync(a);
        var idB = await RecordTestSupport.OwnerIdAsync(b);

        int entriesA;
        await using (var db = app.NewDbContext(idA))
        {
            await AssertOnlyOwnerAsync(db.Accounts, x => x.OwnerId, idA);
            await AssertOnlyOwnerAsync(db.Categories, x => x.OwnerId, idA);
            await AssertOnlyOwnerAsync(db.Entries, x => x.OwnerId, idA);
            await AssertOnlyOwnerAsync(db.Plans, x => x.OwnerId, idA);
            await AssertOnlyOwnerAsync(db.Installments, x => x.OwnerId, idA);
            await AssertOnlyOwnerAsync(db.Payments, x => x.OwnerId, idA);
            await AssertOnlyOwnerAsync(db.PaymentAllocations, x => x.OwnerId, idA);
            await AssertOnlyOwnerAsync(db.Transfers, x => x.OwnerId, idA);
            await AssertOnlyOwnerAsync(db.ImportBatches, x => x.OwnerId, idA);
            await AssertOnlyOwnerAsync(db.ImportedRecords, x => x.OwnerId, idA);
            // Navegaciones e Include también quedan filtradas.
            var installments = (await db.Plans.Include(p => p.Installments).ToListAsync()).SelectMany(p => p.Installments).ToList();
            Assert.All(installments, i => Assert.Equal(idA, i.OwnerId));
            Assert.Equal(0, await db.Installments.CountAsync(i => i.Plan!.Concept == "Plan B"));
            // Un registro ajeno no aparece aunque la consulta lo busque sin filtrar por owner.
            Assert.Null(await db.Entries.SingleOrDefaultAsync(e => e.Concept == "Sueldo B"));
            // Una actualización masiva sin Where alcanza solo al propietario de la sesión.
            entriesA = await db.Entries.CountAsync();
            Assert.Equal(entriesA, await db.Entries.ExecuteUpdateAsync(s => s.SetProperty(e => e.Note, "solo A")));
        }

        await using (var db = app.NewDbContext())
        {
            var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.Entries.CountAsync());
            Assert.Contains("IgnoreQueryFilters", ex.ToString());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.ImportBatches.ToListAsync());
            var all = await db.Entries.IgnoreQueryFilters([AppDbContext.OwnerFilter]).AsNoTracking().ToListAsync();
            Assert.Equal(entriesA, all.Count(e => e.OwnerId == idA && e.Note == "solo A"));
            Assert.Contains(all, e => e.OwnerId == idB);
            Assert.All(all.Where(e => e.OwnerId == idB), e => Assert.Null(e.Note));
        }

        // La CLI operativa recorre todos los propietarios de forma explícita (conteos de restauración).
        var verify = await CliRunner.RunAsync("verify-data", app.ConnectionString, app.KeyRingPath, environment: "Testing");
        Assert.True(verify.Code == 0, verify.Output);
        var counts = JsonDocument.Parse(verify.Output).RootElement.GetProperty("counts");
        await using (var db = app.NewDbContext())
        {
            Assert.Equal(await db.Entries.IgnoreQueryFilters([AppDbContext.OwnerFilter]).CountAsync(), counts.GetProperty("entries").GetInt32());
            Assert.Equal(await db.Installments.IgnoreQueryFilters([AppDbContext.OwnerFilter]).CountAsync(), counts.GetProperty("installments").GetInt32());
        }
        // Y por la API, B sigue sin ver nada de A.
        Assert.DoesNotContain("Sueldo A", (await b.GetJson("/api/v1/entries")).ToString());
    }

    [Fact]
    public async Task Maintenance_job_expires_import_previews_of_every_owner_without_a_session()
    {
        await using var app = await TestApp.StartAsync(pg);
        var a = await app.SignedInAsync("augusto");
        var b = await app.SignedInAsync("intruso", "otra frase larga para el intruso");
        foreach (var (c, tag) in new[] { (a, "A"), (b, "B") })
        {
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/v1/imports/preview", RecordTestSupport.LegacyImport(tag))).StatusCode);
        }
        app.Clock.Advance(TimeSpan.FromMinutes(31));
        var job = new MaintenanceService(app.Services.GetRequiredService<IServiceScopeFactory>(), app.Clock, NullLogger<MaintenanceService>.Instance);
        await job.StartAsync(CancellationToken.None);
        try
        {
            await using var db = app.NewDbContext();
            var batches = db.ImportBatches.IgnoreQueryFilters([AppDbContext.OwnerFilter]).AsNoTracking();
            for (var i = 0; i < 100 && await batches.AnyAsync(x => x.Status == ImportStatus.Preview); i++) await Task.Delay(100);
            var all = await batches.ToListAsync();
            Assert.Equal(2, all.Count);
            Assert.All(all, x =>
            {
                Assert.Equal(ImportStatus.Expired, x.Status);
                Assert.Null(x.NormalizedPayload);
            });
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
    }
}
