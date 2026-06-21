using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveMoveJobDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveDeduplicationKey",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "MoveJobs"
                SET "ActiveDeduplicationKey" =
                    CAST("AudiobookId" AS TEXT) || ':' ||
                    UPPER(RTRIM(REPLACE(TRIM(COALESCE("RequestedPath", '')), '\', '/'), '/'))
                WHERE "Status" IN ('Queued', 'Processing')
                  AND "Id" = (
                      SELECT MIN(candidate."Id")
                      FROM "MoveJobs" AS candidate
                      WHERE candidate."Status" IN ('Queued', 'Processing')
                        AND candidate."AudiobookId" = "MoveJobs"."AudiobookId"
                        AND UPPER(RTRIM(REPLACE(TRIM(COALESCE(candidate."RequestedPath", '')), '\', '/'), '/')) =
                            UPPER(RTRIM(REPLACE(TRIM(COALESCE("MoveJobs"."RequestedPath", '')), '\', '/'), '/'))
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MoveJobs_ActiveDeduplicationKey",
                table: "MoveJobs",
                column: "ActiveDeduplicationKey",
                unique: true,
                filter: "\"ActiveDeduplicationKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MoveJobs_ActiveDeduplicationKey",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "ActiveDeduplicationKey",
                table: "MoveJobs");
        }
    }
}
