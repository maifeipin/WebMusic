using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WebMusic.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityImportBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityImportBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchTag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceReportSha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuditManifestSha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    AppliedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RolledBackAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RolledBackBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityImportItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<int>(type: "integer", nullable: false),
                    MediaFileId = table.Column<int>(type: "integer", nullable: false),
                    TargetTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TargetArtist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TargetAlbum = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TargetDurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    MetadataFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MatchedRecordingId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MatchedReleaseId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MatchedArtistId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MatchedTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MatchedArtist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MatchedDurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    PreSnapshotJson = table.Column<string>(type: "text", nullable: true),
                    CreatedIdentityId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorDetail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityImportItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityImportItems_IdentityImportBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "IdentityImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityImportBatches_BatchTag",
                table: "IdentityImportBatches",
                column: "BatchTag",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityImportBatches_Status",
                table: "IdentityImportBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityImportItems_BatchId_MediaFileId",
                table: "IdentityImportItems",
                columns: new[] { "BatchId", "MediaFileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityImportItems_MediaFileId",
                table: "IdentityImportItems",
                column: "MediaFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityImportItems");

            migrationBuilder.DropTable(
                name: "IdentityImportBatches");
        }
    }
}
