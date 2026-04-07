using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAudiobookshelfSettingsToApplicationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfApiKey",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AudiobookshelfEnabled",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfLibraryId",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AudiobookshelfScanAfterImport",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AudiobookshelfScanOnCompletedDownload",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AudiobookshelfScanOnManualImport",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfUrl",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AudiobookshelfVerifySsl",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudiobookshelfApiKey",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfEnabled",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfLibraryId",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfScanAfterImport",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfScanOnCompletedDownload",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfScanOnManualImport",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfUrl",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfVerifySsl",
                table: "ApplicationSettings");
        }
    }
}
