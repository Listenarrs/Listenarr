using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFilesystemRecoveryIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AudiobookFileId",
                table: "FileMutationJournals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AudiobookDeletionIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeleteFolder = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookDeletionIntents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookDeletionIntents_AudiobookId",
                table: "AudiobookDeletionIntents",
                column: "AudiobookId",
                unique: true,
                filter: "\"State\" <> 'Completed'");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookDeletionIntents_AudiobookId_State",
                table: "AudiobookDeletionIntents",
                columns: new[] { "AudiobookId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookDeletionIntents_UpdatedAt",
                table: "AudiobookDeletionIntents",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudiobookDeletionIntents");

            migrationBuilder.DropColumn(
                name: "AudiobookFileId",
                table: "FileMutationJournals");
        }
    }
}
