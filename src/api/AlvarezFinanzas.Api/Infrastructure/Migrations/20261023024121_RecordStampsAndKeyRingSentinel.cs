using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlvarezFinanzas.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecordStampsAndKeyRingSentinel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // docs/02: todo registro financiero tiene created_at, updated_at y version; cuotas y asignaciones de pago no los
            // tenían. Expand compatible con la versión anterior (rollback sin revertir migraciones): columnas NOT NULL con
            // DEFAULT, que esa versión necesita para seguir insertando. La versión actual siempre los sella explícitamente.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "payment_allocations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "payment_allocations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "payment_allocations",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "installments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "installments",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // Relleno de las filas existentes desde su registro padre (en vez de la hora de la migración): las cuotas nacen con
            // su plan y cambian con los pagos que las cancelan o revierten; las asignaciones son inmutables y nacen con su pago.
            migrationBuilder.Sql("""
                UPDATE installments i
                SET created_at = p.created_at,
                    updated_at = GREATEST(p.updated_at, COALESCE((
                        SELECT max(pay.updated_at)
                        FROM payment_allocations a JOIN payments pay ON pay.id = a.payment_id
                        WHERE a.installment_id = i.id), p.updated_at))
                FROM installment_plans p
                WHERE p.id = i.plan_id;
                UPDATE payment_allocations a
                SET created_at = pay.created_at, updated_at = pay.created_at
                FROM payments pay
                WHERE pay.id = a.payment_id;
                """);

            // Centinela del key ring (KeyRingGuard): fila única con un valor protegido por Data Protection.
            migrationBuilder.CreateTable(
                name: "key_ring_sentinel",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    protected_value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_key_ring_sentinel", x => x.id);
                    table.CheckConstraint("ck_key_ring_sentinel_single", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "key_ring_sentinel");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "payment_allocations");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "payment_allocations");

            migrationBuilder.DropColumn(
                name: "version",
                table: "payment_allocations");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "installments");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "installments");
        }
    }
}
