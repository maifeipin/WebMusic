using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebMusic.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchedWithoutAssetsToEnrichmentJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MatchedWithoutAssets",
                table: "EnrichmentJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Before this column existed, WorkerEnrichmentController counted a
            // MatchedWithoutAssets attempt as Unmatched. Repair persisted job
            // summaries from the append-only audit trail during the upgrade.
            migrationBuilder.Sql(@"
                WITH matched_without_assets AS (
                    SELECT ""JobId"", COUNT(*)::integer AS ""Count""
                    FROM ""EnrichmentAttempts""
                    WHERE ""Outcome"" = 'MatchedWithoutAssets'
                    GROUP BY ""JobId""
                )
                UPDATE ""EnrichmentJobs"" AS job
                SET ""MatchedWithoutAssets"" = source.""Count"",
                    ""Unmatched"" = GREATEST(0, job.""Unmatched"" - source.""Count"")
                FROM matched_without_assets AS source
                WHERE job.""Id"" = source.""JobId"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous schema represented this outcome inside Unmatched.
            // Preserve that legacy meaning if the migration is rolled back.
            migrationBuilder.Sql(@"
                UPDATE ""EnrichmentJobs""
                SET ""Unmatched"" = ""Unmatched"" + ""MatchedWithoutAssets"";
            ");

            migrationBuilder.DropColumn(
                name: "MatchedWithoutAssets",
                table: "EnrichmentJobs");
        }
    }
}
