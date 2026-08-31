using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompatibilityBatchManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExpectedBatchMemberCount",
                table: "CompatibilityFilePublicationJournals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedBatchSourceManifestSha256",
                table: "CompatibilityFilePublicationJournals",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedBatchMemberCount",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "ExpectedBatchSourceManifestSha256",
                table: "CompatibilityFilePublicationJournals");
        }
    }
}
