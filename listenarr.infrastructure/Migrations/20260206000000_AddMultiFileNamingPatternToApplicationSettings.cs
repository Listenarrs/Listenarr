using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Listenarr.Infrastructure.Models;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    [DbContext(typeof(ListenArrDbContext))]
    [Migration("20260206000000_AddMultiFileNamingPatternToApplicationSettings")]
    /// <inheritdoc />
    public partial class AddMultiFileNamingPatternToApplicationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MultiFileNamingPattern",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "{Title}-{DiskNumber:00}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MultiFileNamingPattern",
                table: "ApplicationSettings");
        }
    }
}
