using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WebMusic.Backend.Migrations
{
    /// <inheritdoc />
    public partial class HardenWorkerProtocolAndProviderLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagedCoverPath",
                table: "EnrichmentJobItems",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderQuotaLedgers",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<string>(type: "text", nullable: false),
                    DailyLimit = table.Column<int>(type: "integer", nullable: false),
                    ReservedUnits = table.Column<int>(type: "integer", nullable: false),
                    ConsumedUnits = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderQuotaLedgers", x => new { x.Provider, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "WorkerSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemId = table.Column<int>(type: "integer", nullable: false),
                    SubmissionId = table.Column<string>(type: "text", nullable: false),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerSubmissions_EnrichmentJobItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "EnrichmentJobItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerSubmissions_ItemId_SubmissionId",
                table: "WorkerSubmissions",
                columns: new[] { "ItemId", "SubmissionId" },
                unique: true);

            // Backfill user roles while preserving enrichment-bot as Admin
            migrationBuilder.Sql(@"
                UPDATE ""Users"" SET ""Role"" = 'Admin' WHERE ""IsAdmin"" = TRUE AND (""Role"" IS NULL OR ""Role"" = '');
                UPDATE ""Users"" SET ""Role"" = 'User' WHERE ""IsAdmin"" = FALSE AND (""Role"" IS NULL OR ""Role"" = '');
                UPDATE ""Users"" SET ""Role"" = 'Admin', ""IsAdmin"" = TRUE WHERE ""Username"" = 'enrichment-bot';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderQuotaLedgers");

            migrationBuilder.DropTable(
                name: "WorkerSubmissions");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "StagedCoverPath",
                table: "EnrichmentJobItems");
        }
    }
}
