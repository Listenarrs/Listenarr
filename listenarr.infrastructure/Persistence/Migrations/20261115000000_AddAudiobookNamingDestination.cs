using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    public partial class AddAudiobookNamingDestination : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "NamingRootPath", table: "Audiobooks", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "CustomDestinationPath", table: "Audiobooks", type: "TEXT", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "NamingRootPath", table: "Audiobooks");
            migrationBuilder.DropColumn(name: "CustomDestinationPath", table: "Audiobooks");
        }
    }
}
