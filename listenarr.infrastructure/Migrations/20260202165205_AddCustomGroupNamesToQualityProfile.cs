using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomGroupNamesToQualityProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UsProxyHost",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "UsProxyPassword",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "UsProxyPort",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "UsProxyUsername",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "UseUsProxy",
                table: "ApplicationSettings");

            migrationBuilder.AddColumn<string>(
                name: "CustomGroupNames",
                table: "QualityProfiles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomGroupNames",
                table: "QualityProfiles");

            migrationBuilder.AddColumn<string>(
                name: "UsProxyHost",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsProxyPassword",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsProxyPort",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "UsProxyUsername",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseUsProxy",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
