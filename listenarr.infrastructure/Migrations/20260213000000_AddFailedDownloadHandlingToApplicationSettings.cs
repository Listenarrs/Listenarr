using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Listenarr.Infrastructure.Models;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    [DbContext(typeof(ListenArrDbContext))]
    [Migration("20260213000000_AddFailedDownloadHandlingToApplicationSettings")]
    /// <inheritdoc />
    public partial class AddFailedDownloadHandlingToApplicationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FailedDownloadHandlingEnabled",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "FailedDownloadAutoSearch",
                table: "ApplicationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedDownloadAutoSearch",
                table: "ApplicationSettings");

            migrationBuilder.DropColumn(
                name: "FailedDownloadHandlingEnabled",
                table: "ApplicationSettings");
        }
    }
}
