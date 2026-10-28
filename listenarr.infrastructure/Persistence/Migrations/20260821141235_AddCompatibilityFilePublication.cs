using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompatibilityFilePublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompatibilityFilePublicationJournals",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedAction = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveAction = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceDisposition = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DestinationPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourceLength = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetLength = table.Column<long>(type: "INTEGER", nullable: true),
                    TargetSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsCompanionFile = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompatibilityFilePublicationJournals", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompatibilityFilePublicationJournals_AudiobookId",
                table: "CompatibilityFilePublicationJournals",
                column: "AudiobookId");

            migrationBuilder.CreateIndex(
                name: "IX_CompatibilityFilePublicationJournals_State",
                table: "CompatibilityFilePublicationJournals",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompatibilityFilePublicationJournals");
        }
    }
}
