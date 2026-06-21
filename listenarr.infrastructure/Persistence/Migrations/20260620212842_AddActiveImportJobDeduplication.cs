using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveImportJobDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveDeduplicationKey",
                table: "DownloadProcessingJobs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "DownloadProcessingJobs"
                SET "ActiveDeduplicationKey" = UPPER(TRIM("DownloadId"))
                WHERE "Status" IN (0, 1, 4)
                  AND "Id" = (
                      SELECT MIN(candidate."Id")
                      FROM "DownloadProcessingJobs" AS candidate
                      WHERE candidate."Status" IN (0, 1, 4)
                        AND UPPER(TRIM(candidate."DownloadId")) =
                            UPPER(TRIM("DownloadProcessingJobs"."DownloadId"))
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadProcessingJobs_ActiveDeduplicationKey",
                table: "DownloadProcessingJobs",
                column: "ActiveDeduplicationKey",
                unique: true,
                filter: "\"ActiveDeduplicationKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DownloadProcessingJobs_ActiveDeduplicationKey",
                table: "DownloadProcessingJobs");

            migrationBuilder.DropColumn(
                name: "ActiveDeduplicationKey",
                table: "DownloadProcessingJobs");
        }
    }
}
