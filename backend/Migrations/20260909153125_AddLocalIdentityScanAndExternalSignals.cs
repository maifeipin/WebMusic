using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WebMusic.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalIdentityScanAndExternalSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaExternalReferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaFileId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CanonicalUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    MatchMethod = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MatchConfidence = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MetadataFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaExternalReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaExternalReferences_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaIdentityScanStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaFileId = table.Column<int>(type: "integer", nullable: false),
                    InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    RecordingId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MirrorVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastReportSha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetryAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaIdentityScanStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaIdentityScanStates_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaExternalSignals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaExternalReferenceId = table.Column<int>(type: "integer", nullable: false),
                    SignalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RawValue = table.Column<double>(type: "double precision", nullable: true),
                    NormalizedScore = table.Column<double>(type: "double precision", nullable: true),
                    SampleSize = table.Column<long>(type: "bigint", nullable: true),
                    RawMetricsJson = table.Column<string>(type: "text", nullable: true),
                    AlgorithmVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SourcePayloadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RefreshAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaExternalSignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaExternalSignals_MediaExternalReferences_MediaExternalR~",
                        column: x => x.MediaExternalReferenceId,
                        principalTable: "MediaExternalReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaExternalReferences_MediaFileId_Provider_SubjectType",
                table: "MediaExternalReferences",
                columns: new[] { "MediaFileId", "Provider", "SubjectType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaExternalReferences_Provider_ExternalId",
                table: "MediaExternalReferences",
                columns: new[] { "Provider", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaExternalSignals_MediaExternalReferenceId_SignalKey",
                table: "MediaExternalSignals",
                columns: new[] { "MediaExternalReferenceId", "SignalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaIdentityScanStates_MediaFileId",
                table: "MediaIdentityScanStates",
                column: "MediaFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaIdentityScanStates_Outcome_RetryAfter",
                table: "MediaIdentityScanStates",
                columns: new[] { "Outcome", "RetryAfter" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaExternalSignals");

            migrationBuilder.DropTable(
                name: "MediaIdentityScanStates");

            migrationBuilder.DropTable(
                name: "MediaExternalReferences");
        }
    }
}
