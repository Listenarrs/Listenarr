using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedFileRenameJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VerifiedFileRenameJournals",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    AudiobookFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedBatchMemberCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedBatchManifestSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DestinationPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    StagingPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    RetirementPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourceLength = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceRootFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceStorageContractRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    DestinationRootFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    DestinationStorageContractRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerifiedFileRenameJournals", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VerifiedFileRenameJournals_AudiobookId",
                table: "VerifiedFileRenameJournals",
                column: "AudiobookId");

            migrationBuilder.CreateIndex(
                name: "IX_VerifiedFileRenameJournals_BatchId",
                table: "VerifiedFileRenameJournals",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_VerifiedFileRenameJournals_State",
                table: "VerifiedFileRenameJournals",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VerifiedFileRenameJournals");
        }
    }
}
