using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateDownloadProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Protocol",
                table: "Indexers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
            @"
                UPDATE Indexers
                SET Protocol = CASE 
                    WHEN LOWER(Type) = 'ddl' THEN 'DirectDownload'
                    ELSE Type
                END;
            ");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Indexers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Indexers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
            
            migrationBuilder.Sql(
            @"
                UPDATE Indexers
                SET Type = CASE 
                    WHEN Protocol = 'DirectDownload' THEN 'DDL'
                    ELSE Protocol
                END;
            ");

            migrationBuilder.DropColumn(
                name: "Protocol",
                table: "Indexers");
        }
    }
}