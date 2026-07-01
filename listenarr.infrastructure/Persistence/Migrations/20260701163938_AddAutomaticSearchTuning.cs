using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticSearchTuning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutomaticSearchBookDelaySeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "AutomaticSearchIntervalHours",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<bool>(
                name: "AutomaticSearchTitleOnlyFallback",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomaticSearchBookDelaySeconds",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AutomaticSearchIntervalHours",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AutomaticSearchTitleOnlyFallback",
                table: "ApplicationSettings");
        }
    }
}
