using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeakStorageVerifiedCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StorageContractRevision",
                table: "RootFolders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WeakStoragePolicyRevision",
                table: "RootFolders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WeakStorageSourceCleanupPolicy",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "RetainSource");

            migrationBuilder.AddColumn<bool>(
                name: "ForceCopyAndRetainSource",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SourceCleanupMode",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "RetainSource");

            migrationBuilder.AddColumn<int>(
                name: "SourcePolicyRevision",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceRootFolderId",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceStorageContractRevision",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetPolicyRevision",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetRootFolderId",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetStorageContractRevision",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "CompatibilityFilePublicationJournals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CleanupOwner",
                table: "CompatibilityFilePublicationJournals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DestinationPolicyRevision",
                table: "CompatibilityFilePublicationJournals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationRootFolderId",
                table: "CompatibilityFilePublicationJournals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationStorageContractRevision",
                table: "CompatibilityFilePublicationJournals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuarantinePath",
                table: "CompatibilityFilePublicationJournals",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourcePolicyRevision",
                table: "CompatibilityFilePublicationJournals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceRootFolderId",
                table: "CompatibilityFilePublicationJournals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceStorageContractRevision",
                table: "CompatibilityFilePublicationJournals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WeakStorageScanCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScanToken = table.Column<Guid>(type: "TEXT", nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    AudiobookFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedStoredPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    ExpectedResolvedPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    ExpectedPhysicalObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeakStorageScanCandidates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompatibilityFilePublicationJournals_BatchId",
                table: "CompatibilityFilePublicationJournals",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_WeakStorageScanCandidates_AudiobookId_ConfirmedAt_ExpiresAt",
                table: "WeakStorageScanCandidates",
                columns: new[] { "AudiobookId", "ConfirmedAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WeakStorageScanCandidates_ScanToken",
                table: "WeakStorageScanCandidates",
                column: "ScanToken");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeakStorageScanCandidates");

            migrationBuilder.DropIndex(
                name: "IX_CompatibilityFilePublicationJournals_BatchId",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "StorageContractRevision",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "WeakStoragePolicyRevision",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "WeakStorageSourceCleanupPolicy",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "ForceCopyAndRetainSource",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceCleanupMode",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourcePolicyRevision",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceRootFolderId",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceStorageContractRevision",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetPolicyRevision",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetRootFolderId",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetStorageContractRevision",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "CleanupOwner",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "DestinationPolicyRevision",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "DestinationRootFolderId",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "DestinationStorageContractRevision",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "QuarantinePath",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "SourcePolicyRevision",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "SourceRootFolderId",
                table: "CompatibilityFilePublicationJournals");

            migrationBuilder.DropColumn(
                name: "SourceStorageContractRevision",
                table: "CompatibilityFilePublicationJournals");
        }
    }
}
