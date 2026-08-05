using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryDirectoryOwnershipRootForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_LibraryDirectoryOwnerships_RootFolders_ManagedRootFolderId",
                table: "LibraryDirectoryOwnerships",
                column: "ManagedRootFolderId",
                principalTable: "RootFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LibraryDirectoryOwnerships_RootFolders_ManagedRootFolderId",
                table: "LibraryDirectoryOwnerships");
        }
    }
}
