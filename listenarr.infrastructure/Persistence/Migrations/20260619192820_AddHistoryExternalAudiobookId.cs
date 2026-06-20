using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryExternalAudiobookId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudiobookExternalId",
                table: "History",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE History
                SET AudiobookExternalId = (
                    SELECT CAST(DownloadHistories.AudiobookId AS TEXT)
                    FROM DownloadHistories
                    WHERE upper(DownloadHistories.DownloadId) = upper(History.DownloadId)
                      AND DownloadHistories.DownloadClientId = History.DownloadClientId
                      AND DownloadHistories.AudiobookId IS NOT NULL
                    ORDER BY DownloadHistories.EventDate DESC
                    LIMIT 1
                )
                WHERE DownloadId IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_History_AudiobookExternalId",
                table: "History",
                column: "AudiobookExternalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_History_AudiobookExternalId",
                table: "History");

            migrationBuilder.DropColumn(
                name: "AudiobookExternalId",
                table: "History");
        }
    }
}
