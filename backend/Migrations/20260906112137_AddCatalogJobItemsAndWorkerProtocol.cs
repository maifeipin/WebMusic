using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WebMusic.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogJobItemsAndWorkerProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverStatus",
                table: "MediaIdentities",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LyricsStatus",
                table: "MediaIdentities",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CatalogBookmarkId",
                table: "EnrichmentJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputFingerprint",
                table: "EnrichmentAttempts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetryAfter",
                table: "EnrichmentAttempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EnrichmentJobItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<string>(type: "text", nullable: false),
                    MediaFileId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    InputFingerprint = table.Column<string>(type: "text", nullable: true),
                    CoverStatus = table.Column<string>(type: "text", nullable: false),
                    LyricsStatus = table.Column<string>(type: "text", nullable: false),
                    WorkerNodeId = table.Column<string>(type: "text", nullable: true),
                    LeasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrichmentJobItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnrichmentJobItems_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentJobItems_JobId_Status",
                table: "EnrichmentJobItems",
                columns: new[] { "JobId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentJobItems_MediaFileId",
                table: "EnrichmentJobItems",
                column: "MediaFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnrichmentJobItems");

            migrationBuilder.DropColumn(
                name: "CoverStatus",
                table: "MediaIdentities");

            migrationBuilder.DropColumn(
                name: "LyricsStatus",
                table: "MediaIdentities");

            migrationBuilder.DropColumn(
                name: "CatalogBookmarkId",
                table: "EnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "InputFingerprint",
                table: "EnrichmentAttempts");

            migrationBuilder.DropColumn(
                name: "RetryAfter",
                table: "EnrichmentAttempts");
        }
    }
}
