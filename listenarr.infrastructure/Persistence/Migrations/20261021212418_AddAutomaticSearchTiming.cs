using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticSearchTiming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Column defaults match the ApplicationSettings property initializers so the
            // existing singleton settings row behaves exactly like a fresh install.
            migrationBuilder.AddColumn<int>(
                name: "AutomaticSearchBookDelaySeconds",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "AutomaticSearchCooldownHours",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<int>(
                name: "AutomaticSearchIntervalHours",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomaticSearchBookDelaySeconds",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AutomaticSearchCooldownHours",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "AutomaticSearchIntervalHours",
                table: "ApplicationSettings");
        }
    }
}
