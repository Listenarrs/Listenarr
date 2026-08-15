using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableFilesystemRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaseSensitivityMode",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<string>(
                name: "DirectoryObjectIdentity",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectoryObjectIdentityUnavailableReason",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DirectoryObjectIdentityVersion",
                table: "RootFolders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityKey",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityState",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unavailable");

            migrationBuilder.AddColumn<string>(
                name: "ResolvedCaseSensitivity",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<bool>(
                name: "DeleteEmptySource",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionProtocolVersion",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FailureKind",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<int>(
                name: "IdentityKeyVersion",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAt",
                table: "MoveJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LeaseGeneration",
                table: "MoveJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "MoveJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<Guid>(
                name: "RelocationId",
                table: "MoveJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceCaseSensitivity",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceCaseSensitivityMode",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceCleanupBoundary",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceDirectoryCleanupState",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 24,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<string>(
                name: "SourceDirectoryObjectIdentity",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceIdentityBoundary",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePathSyntax",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetCaseSensitivity",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetCaseSensitivityMode",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDirectoryObjectIdentity",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetIdentityBoundary",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetPathSyntax",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "History",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CanonicalPath",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathCaseSensitivity",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "PathCaseSensitivityMode",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityBoundary",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityLookupKey",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityReason",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityState",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unavailable");

            migrationBuilder.AddColumn<int>(
                name: "PathIdentityVersion",
                table: "AudiobookFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "PathOwnershipKey",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathSyntax",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PhysicalIdentityObservedAtUtc",
                table: "AudiobookFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhysicalIdentityVersion",
                table: "AudiobookFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "PhysicalObjectIdentity",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 512,
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

            migrationBuilder.CreateTable(
                name: "FileMutationJournals",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    Action = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DestinationPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourcePhysicalObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TargetPhysicalObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SourceLength = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: true),
                    AudiobookFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileMutationJournals", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "LibraryDirectoryOwnerships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CanonicalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    PathSyntax = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PathCaseSensitivity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PathCaseSensitivityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PathIdentityBoundary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    PathIdentityLookupKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    PathOwnershipKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    OwnershipToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreationWorkflow = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreationOperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: true),
                    ManagedRootFolderId = table.Column<int>(type: "INTEGER", nullable: true),
                    DirectoryObjectIdentityVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    DirectoryObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DirectoryObjectIdentityUnavailableReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    StateReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryDirectoryOwnerships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryDirectoryOwnerships_RootFolders_ManagedRootFolderId",
                        column: x => x.ManagedRootFolderId,
                        principalTable: "RootFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MoveJobCreatedDirectories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MoveJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DirectoryObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoveJobCreatedDirectories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoveJobCreatedDirectories_MoveJobs_MoveJobId",
                        column: x => x.MoveJobId,
                        principalTable: "MoveJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MoveJobEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MoveJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    EntryType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CopyState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CleanupState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CleanupProtectionVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SourcePhysicalObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    TargetPhysicalObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoveJobEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoveJobEntries_MoveJobs_MoveJobId",
                        column: x => x.MoveJobId,
                        principalTable: "MoveJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MoveScanHandoffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MoveJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    AttemptGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LeaseGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActiveScanJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoveScanHandoffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoveScanHandoffs_MoveJobs_MoveJobId",
                        column: x => x.MoveJobId,
                        principalTable: "MoveJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RootFolderRelocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RootFolderId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActiveRootFolderId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SourceCaseSensitivityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "Auto"),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    DeleteEmptySource = table.Column<bool>(type: "INTEGER", nullable: false),
                    DesiredName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DesiredIsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetCaseSensitivityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetIdentityEnrollmentState = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false, defaultValue: "Authorized"),
                    TargetDirectoryObjectIdentityVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetDirectoryObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TargetDirectoryObjectIdentityUnavailableReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    TotalJobs = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedJobs = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RootFolderRelocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RootFolderRelocations_RootFolders_RootFolderId",
                        column: x => x.RootFolderId,
                        principalTable: "RootFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "LibraryDirectoryOwnershipPathMigrations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnershipId = table.Column<long>(type: "INTEGER", nullable: false),
                    RelocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceCanonicalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourcePathSyntax = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceCaseSensitivity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceCaseSensitivityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceIdentityBoundary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourceIdentityLookupKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    SourceOwnershipKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    TargetCanonicalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    TargetPathSyntax = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetCaseSensitivity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetCaseSensitivityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetIdentityBoundary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    TargetIdentityLookupKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    TargetOwnershipKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryDirectoryOwnershipPathMigrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryDirectoryOwnershipPathMigrations_LibraryDirectoryOwnerships_OwnershipId",
                        column: x => x.OwnershipId,
                        principalTable: "LibraryDirectoryOwnerships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LibraryDirectoryOwnershipPathMigrations_RootFolderRelocations_RelocationId",
                        column: x => x.RelocationId,
                        principalTable: "RootFolderRelocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RootFolderRelocationCreatedDirectories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RelocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    OwnershipToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DirectoryObjectIdentityVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    DirectoryObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RootFolderRelocationCreatedDirectories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RootFolderRelocationCreatedDirectories_RootFolderRelocations_RelocationId",
                        column: x => x.RelocationId,
                        principalTable: "RootFolderRelocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RootFolderRelocationSkippedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RootFolderRelocationSkippedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RootFolderRelocationSkippedItems_RootFolderRelocations_RelocationId",
                        column: x => x.RelocationId,
                        principalTable: "RootFolderRelocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RootFolders_PathIdentityKey",
                table: "RootFolders",
                column: "PathIdentityKey",
                unique: true,
                filter: "\"PathIdentityKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RootFolders_SingleDefault",
                table: "RootFolders",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_MoveJobs_RelocationId",
                table: "MoveJobs",
                column: "RelocationId");

            migrationBuilder.CreateIndex(
                name: "IX_MoveJobs_Status_NextAttemptAt_LeaseExpiresAt",
                table: "MoveJobs",
                columns: new[] { "Status", "NextAttemptAt", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_History_IdempotencyKey",
                table: "History",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookFiles_PathIdentityLookupKey",
                table: "AudiobookFiles",
                column: "PathIdentityLookupKey");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookFiles_PathOwnershipKey",
                table: "AudiobookFiles",
                column: "PathOwnershipKey",
                unique: true,
                filter: "\"PathOwnershipKey\" IS NOT NULL");

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

            migrationBuilder.CreateIndex(
                name: "IX_FileMutationJournals_State",
                table: "FileMutationJournals",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_FileMutationJournals_UpdatedAt",
                table: "FileMutationJournals",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnershipPathMigrations_OwnershipId_RelocationId",
                table: "LibraryDirectoryOwnershipPathMigrations",
                columns: new[] { "OwnershipId", "RelocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnershipPathMigrations_RelocationId",
                table: "LibraryDirectoryOwnershipPathMigrations",
                column: "RelocationId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnershipPathMigrations_TargetOwnershipKey",
                table: "LibraryDirectoryOwnershipPathMigrations",
                column: "TargetOwnershipKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_CreationOperationId_State",
                table: "LibraryDirectoryOwnerships",
                columns: new[] { "CreationOperationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_ManagedRootFolderId",
                table: "LibraryDirectoryOwnerships",
                column: "ManagedRootFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_OwnershipToken",
                table: "LibraryDirectoryOwnerships",
                column: "OwnershipToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_PathIdentityLookupKey",
                table: "LibraryDirectoryOwnerships",
                column: "PathIdentityLookupKey");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_PathOwnershipKey",
                table: "LibraryDirectoryOwnerships",
                column: "PathOwnershipKey",
                unique: true,
                filter: "\"PathOwnershipKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MoveJobCreatedDirectories_MoveJobId_Path",
                table: "MoveJobCreatedDirectories",
                columns: new[] { "MoveJobId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoveJobEntries_MoveJobId_RelativePath",
                table: "MoveJobEntries",
                columns: new[] { "MoveJobId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoveScanHandoffs_MoveJobId",
                table: "MoveScanHandoffs",
                column: "MoveJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoveScanHandoffs_Status_NextAttemptAt_LeaseExpiresAt",
                table: "MoveScanHandoffs",
                columns: new[] { "Status", "NextAttemptAt", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RootFolderRelocationCreatedDirectories_OwnershipToken",
                table: "RootFolderRelocationCreatedDirectories",
                column: "OwnershipToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RootFolderRelocationCreatedDirectories_RelocationId_CanonicalPath",
                table: "RootFolderRelocationCreatedDirectories",
                columns: new[] { "RelocationId", "CanonicalPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RootFolderRelocations_ActiveRootFolderId",
                table: "RootFolderRelocations",
                column: "ActiveRootFolderId",
                unique: true,
                filter: "\"ActiveRootFolderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RootFolderRelocations_RootFolderId",
                table: "RootFolderRelocations",
                column: "RootFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_RootFolderRelocationSkippedItems_RelocationId_AudiobookId",
                table: "RootFolderRelocationSkippedItems",
                columns: new[] { "RelocationId", "AudiobookId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudiobookDeletionIntents");

            migrationBuilder.DropTable(
                name: "FileMutationJournals");

            migrationBuilder.DropTable(
                name: "LibraryDirectoryOwnershipPathMigrations");

            migrationBuilder.DropTable(
                name: "MoveJobCreatedDirectories");

            migrationBuilder.DropTable(
                name: "MoveJobEntries");

            migrationBuilder.DropTable(
                name: "MoveScanHandoffs");

            migrationBuilder.DropTable(
                name: "RootFolderRelocationCreatedDirectories");

            migrationBuilder.DropTable(
                name: "RootFolderRelocationSkippedItems");

            migrationBuilder.DropTable(
                name: "LibraryDirectoryOwnerships");

            migrationBuilder.DropTable(
                name: "RootFolderRelocations");

            migrationBuilder.DropIndex(
                name: "IX_RootFolders_PathIdentityKey",
                table: "RootFolders");

            migrationBuilder.DropIndex(
                name: "IX_RootFolders_SingleDefault",
                table: "RootFolders");

            migrationBuilder.DropIndex(
                name: "IX_MoveJobs_RelocationId",
                table: "MoveJobs");

            migrationBuilder.DropIndex(
                name: "IX_MoveJobs_Status_NextAttemptAt_LeaseExpiresAt",
                table: "MoveJobs");

            migrationBuilder.DropIndex(
                name: "IX_History_IdempotencyKey",
                table: "History");

            migrationBuilder.DropIndex(
                name: "IX_AudiobookFiles_PathIdentityLookupKey",
                table: "AudiobookFiles");

            migrationBuilder.DropIndex(
                name: "IX_AudiobookFiles_PathOwnershipKey",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "CaseSensitivityMode",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentity",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentityUnavailableReason",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentityVersion",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "PathIdentityKey",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "PathIdentityState",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "ResolvedCaseSensitivity",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "DeleteEmptySource",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "ExecutionProtocolVersion",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "FailureKind",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "IdentityKeyVersion",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "LeaseGeneration",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "RelocationId",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceCaseSensitivity",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceCaseSensitivityMode",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceCleanupBoundary",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceDirectoryCleanupState",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceDirectoryObjectIdentity",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceIdentityBoundary",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourcePathSyntax",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetCaseSensitivity",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetCaseSensitivityMode",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetDirectoryObjectIdentity",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetIdentityBoundary",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetPathSyntax",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "History");

            migrationBuilder.DropColumn(
                name: "CanonicalPath",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathCaseSensitivity",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathCaseSensitivityMode",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityBoundary",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityLookupKey",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityReason",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityState",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityVersion",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathOwnershipKey",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathSyntax",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PhysicalIdentityObservedAtUtc",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PhysicalIdentityVersion",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PhysicalObjectIdentity",
                table: "AudiobookFiles");
        }
    }
}
