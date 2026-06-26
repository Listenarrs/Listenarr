using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybackState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Finished",
                table: "Audiobooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PlaybackFileIndex",
                table: "Audiobooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "PlaybackPositionSeconds",
                table: "Audiobooks",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlaybackUpdatedUtc",
                table: "Audiobooks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Finished",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "PlaybackFileIndex",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "PlaybackPositionSeconds",
                table: "Audiobooks");

            migrationBuilder.DropColumn(
                name: "PlaybackUpdatedUtc",
                table: "Audiobooks");
        }
    }
}
