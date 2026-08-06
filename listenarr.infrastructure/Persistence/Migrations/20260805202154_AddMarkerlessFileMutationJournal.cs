using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarkerlessFileMutationJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileMutationJournals",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 2),
                    Action = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DestinationPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourcePhysicalObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TargetPhysicalObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SourceLength = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileMutationJournals", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileMutationJournals_State",
                table: "FileMutationJournals",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_FileMutationJournals_UpdatedAt",
                table: "FileMutationJournals",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileMutationJournals");
        }
    }
}
