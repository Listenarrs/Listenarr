using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileMutationParentGenerationProofs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationParentDirectoryObjectIdentity",
                table: "FileMutationJournals",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceParentDirectoryObjectIdentity",
                table: "FileMutationJournals",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DestinationParentDirectoryObjectIdentity",
                table: "FileMutationJournals");

            migrationBuilder.DropColumn(
                name: "SourceParentDirectoryObjectIdentity",
                table: "FileMutationJournals");
        }
    }
}
