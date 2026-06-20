using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnifiedActionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "History",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DownloadClientId",
                table: "History",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DownloadId",
                table: "History",
                type: "TEXT",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "History",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Outcome",
                table: "History",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ParentEventId",
                table: "History",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTitle",
                table: "History",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HistoryRetentionDays",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Existing general history is retained in place and receives stable correlation IDs.
            migrationBuilder.Sql(
                """
                UPDATE History
                SET CorrelationId = 'legacy-history-' || Id,
                    Outcome = 1,
                    SourceTitle = COALESCE(AudiobookTitle, Message)
                WHERE CorrelationId = '';
                """);

            // Copy the legacy download-only ledger into the canonical History table.
            // The old table remains for rollback compatibility but receives no new writes.
            migrationBuilder.Sql(
                """
                INSERT INTO History
                    (AudiobookId, AudiobookTitle, EventType, Message, Source, Timestamp,
                     NotificationSent, Data, Outcome, DownloadId, DownloadClientId,
                     CorrelationId, ParentEventId, Error, SourceTitle)
                SELECT
                    NULL,
                    Title,
                    CASE EventType
                        WHEN 1 THEN 'Grabbed'
                        WHEN 2 THEN 'Downloading'
                        WHEN 3 THEN 'DownloadCompleted'
                        WHEN 4 THEN 'DownloadFailed'
                        WHEN 5 THEN 'Imported'
                        WHEN 6 THEN 'ImportFailed'
                        WHEN 7 THEN 'Paused'
                        WHEN 8 THEN 'Resumed'
                        WHEN 9 THEN 'Removed'
                        WHEN 10 THEN 'Checking'
                        ELSE 'Warning'
                    END,
                    ErrorMessage,
                    COALESCE(NULLIF(DownloadClient, ''), 'LegacyDownloadHistory'),
                    EventDate,
                    0,
                    Data,
                    CASE WHEN EventType IN (4, 6) THEN 2 ELSE 1 END,
                    upper(DownloadId),
                    DownloadClientId,
                    CASE
                        WHEN DownloadId IS NULL OR DownloadId = '' THEN 'legacy-download-history-' || Id
                        ELSE upper(DownloadId)
                    END,
                    NULL,
                    ErrorMessage,
                    Title
                FROM DownloadHistories;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_History_CorrelationId",
                table: "History",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_History_DownloadClientId",
                table: "History",
                column: "DownloadClientId");

            migrationBuilder.CreateIndex(
                name: "IX_History_DownloadId",
                table: "History",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_History_EventType",
                table: "History",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_History_Outcome",
                table: "History",
                column: "Outcome");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_History_CorrelationId",
                table: "History");

            migrationBuilder.DropIndex(
                name: "IX_History_DownloadClientId",
                table: "History");

            migrationBuilder.DropIndex(
                name: "IX_History_DownloadId",
                table: "History");

            migrationBuilder.DropIndex(
                name: "IX_History_EventType",
                table: "History");

            migrationBuilder.DropIndex(
                name: "IX_History_Outcome",
                table: "History");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "History");

            migrationBuilder.DropColumn(
                name: "DownloadClientId",
                table: "History");

            migrationBuilder.DropColumn(
                name: "DownloadId",
                table: "History");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "History");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "History");

            migrationBuilder.DropColumn(
                name: "ParentEventId",
                table: "History");

            migrationBuilder.DropColumn(
                name: "SourceTitle",
                table: "History");

            migrationBuilder.DropColumn(
                name: "HistoryRetentionDays",
                table: "ApplicationSettings");
        }
    }
}
