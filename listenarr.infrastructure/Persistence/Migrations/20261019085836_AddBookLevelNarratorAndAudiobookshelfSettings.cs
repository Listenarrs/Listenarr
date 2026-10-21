using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBookLevelNarratorAndAudiobookshelfSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequiredNarrator",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfApiKey",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudiobookshelfBaseUrl",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiredNarrator",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfApiKey",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AudiobookshelfBaseUrl",
                table: "ApplicationSettings");
        }
    }
}
