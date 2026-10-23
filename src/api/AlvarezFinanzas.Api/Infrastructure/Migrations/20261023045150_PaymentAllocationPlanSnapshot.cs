using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlvarezFinanzas.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaymentAllocationPlanSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Historial de pagos revertidos: al acortar un plan, la asignación a una cuota que deja de existir ya no se
            // borra; deja de apuntar a la cuota y conserva plan y número. Expand compatible con la versión anterior
            // (rollback sin revertir migraciones): columnas opcionales, y una asignación a cuota sin copia sigue siendo
            // válida (así inserta esa versión; la actual lee el plan de la cuota cuando falta la copia).
            migrationBuilder.DropCheckConstraint(
                name: "ck_allocations_target",
                table: "payment_allocations");

            migrationBuilder.AddColumn<int>(
                name: "installment_number",
                table: "payment_allocations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "plan_id",
                table: "payment_allocations",
                type: "uuid",
                nullable: true);

            // Las asignaciones existentes copian plan y número de su cuota.
            migrationBuilder.Sql("""
                UPDATE payment_allocations AS a
                SET plan_id = i.plan_id, installment_number = i.number
                FROM installments AS i
                WHERE a.installment_id = i.id AND a.owner_id = i.owner_id;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_payment_allocations_owner_id_plan_id",
                table: "payment_allocations",
                columns: new[] { "owner_id", "plan_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_allocations_installment_number",
                table: "payment_allocations",
                sql: "installment_number IS NULL OR installment_number >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_allocations_target",
                table: "payment_allocations",
                sql: "NOT (installment_id IS NOT NULL AND entry_id IS NOT NULL) AND (entry_id IS NULL OR (plan_id IS NULL AND installment_number IS NULL)) AND (installment_id IS NOT NULL OR entry_id IS NOT NULL OR (plan_id IS NOT NULL AND installment_number IS NOT NULL))");

            migrationBuilder.AddForeignKey(
                name: "fk_payment_allocations_installment_plans_owner_id_plan_id",
                table: "payment_allocations",
                columns: new[] { "owner_id", "plan_id" },
                principalTable: "installment_plans",
                principalColumns: new[] { "owner_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payment_allocations_installment_plans_owner_id_plan_id",
                table: "payment_allocations");

            migrationBuilder.DropIndex(
                name: "ix_payment_allocations_owner_id_plan_id",
                table: "payment_allocations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_allocations_installment_number",
                table: "payment_allocations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_allocations_target",
                table: "payment_allocations");

            // La restricción anterior exige cuota o gasto: las asignaciones de pagos revertidos cuya cuota dejó de existir
            // vuelven a quitarse, como hacía la versión anterior al acortar el plan.
            migrationBuilder.Sql("DELETE FROM payment_allocations WHERE installment_id IS NULL AND entry_id IS NULL;");

            migrationBuilder.DropColumn(
                name: "installment_number",
                table: "payment_allocations");

            migrationBuilder.DropColumn(
                name: "plan_id",
                table: "payment_allocations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_allocations_target",
                table: "payment_allocations",
                sql: "(installment_id IS NULL) <> (entry_id IS NULL)");
        }
    }
}
