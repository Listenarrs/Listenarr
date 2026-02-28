using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Listenarr.Infrastructure.Models;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    [DbContext(typeof(ListenArrDbContext))]
    [Migration("20260204000000_AddFolderNamingPatternToApplicationSettings")]
    /// <inheritdoc />
    public partial class AddFolderNamingPatternToApplicationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FolderNamingPattern",
                table: "ApplicationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "{Author}/{Series}/{Title}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolderNamingPattern",
                table: "ApplicationSettings");
        }
    }
}
