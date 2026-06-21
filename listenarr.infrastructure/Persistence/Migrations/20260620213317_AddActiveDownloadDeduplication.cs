using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveDownloadDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveAudiobookDeduplicationKey",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Downloads"
                SET "ActiveAudiobookDeduplicationKey" = "AudiobookId"
                WHERE "AudiobookId" IS NOT NULL
                  AND "Status" IN (0, 1, 2, 3, 5, 6, 9)
                  AND "Id" = (
                      SELECT MIN(candidate."Id")
                      FROM "Downloads" AS candidate
                      WHERE candidate."AudiobookId" = "Downloads"."AudiobookId"
                        AND candidate."Status" IN (0, 1, 2, 3, 5, 6, 9)
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_ActiveAudiobookDeduplicationKey",
                table: "Downloads",
                column: "ActiveAudiobookDeduplicationKey",
                unique: true,
                filter: "\"ActiveAudiobookDeduplicationKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Downloads_ActiveAudiobookDeduplicationKey",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "ActiveAudiobookDeduplicationKey",
                table: "Downloads");
        }
    }
}
