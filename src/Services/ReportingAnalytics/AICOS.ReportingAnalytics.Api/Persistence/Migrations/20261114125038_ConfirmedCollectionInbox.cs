using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AICOS.ReportingAnalytics.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmedCollectionInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collection_facts",
                schema: "reporting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_facts", x => x.id);
                    table.CheckConstraint("ck_collection_fact_amount", "amount > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_collection_facts_tenant_id_branch_id_paid_at_utc",
                schema: "reporting",
                table: "collection_facts",
                columns: new[] { "tenant_id", "branch_id", "paid_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM reporting.collection_facts) THEN RAISE EXCEPTION 'Nonempty reporting rollback refused; retain receipt inbox'; END IF; END $$;");
            migrationBuilder.DropTable(
                name: "collection_facts",
                schema: "reporting");
        }
    }
}
