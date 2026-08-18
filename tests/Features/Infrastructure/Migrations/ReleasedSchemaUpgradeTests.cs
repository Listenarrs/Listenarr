/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

public sealed class ReleasedSchemaUpgradeTests
{
    [Fact]
    public async Task PreviousMoveSchema_PreservesLegacyJournalProtocolAndDefaultsNewRowsToCurrent()
    {
        var databasePath = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"journal-parent-proof-upgrade-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var legacyOperationId = Guid.NewGuid();
        var currentOperationId = Guid.NewGuid();
        var rawFallbackOperationId = Guid.NewGuid();

        try
        {
            await using (var db = new ListenArrDbContext(options))
            {
                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync(
                    "20260810160640_AddMoveJobRelocationForeignKey");
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "FileMutationJournals" (
                        "OperationId", "Action", "SourcePath", "DestinationPath",
                        "SourcePhysicalObjectIdentity", "SourceLength", "State",
                        "CreatedAt", "UpdatedAt")
                    VALUES (
                        {legacyOperationId}, {"Move"}, {"/legacy/source.m4b"}, {"/legacy/target.m4b"},
                        {"legacy-source-generation"}, {5L}, {"Planned"},
                        {DateTime.UtcNow}, {DateTime.UtcNow});
                    """);
                await migrator.MigrateAsync();
            }

            await using (var verified = new ListenArrDbContext(options))
            {
                var legacy = await verified.FileMutationJournals
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.OperationId == legacyOperationId);
                Assert.Equal(
                    FileMutationProtocol.MarkerlessDatabaseState,
                    legacy.ProtocolVersion);
                Assert.Equal(string.Empty, legacy.SourceParentDirectoryObjectIdentity);
                Assert.Equal(string.Empty, legacy.DestinationParentDirectoryObjectIdentity);

                verified.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = currentOperationId,
                    Action = FileAction.Move,
                    SourcePath = "/current/source.m4b",
                    DestinationPath = "/current/target.m4b",
                    SourceParentDirectoryObjectIdentity = "source-parent-generation",
                    DestinationParentDirectoryObjectIdentity = "destination-parent-generation",
                    SourcePhysicalObjectIdentity = "current-source-generation",
                    SourceLength = 7,
                    State = FileMutationJournalState.Planned
                });
                await verified.SaveChangesAsync();

                await verified.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "FileMutationJournals" (
                        "OperationId", "Action", "SourcePath", "DestinationPath",
                        "SourceParentDirectoryObjectIdentity",
                        "DestinationParentDirectoryObjectIdentity",
                        "SourcePhysicalObjectIdentity", "SourceLength", "State",
                        "CreatedAt", "UpdatedAt")
                    VALUES (
                        {rawFallbackOperationId}, {"Move"}, {"/raw/source.m4b"}, {"/raw/target.m4b"},
                        {"raw-source-parent-generation"}, {"raw-destination-parent-generation"},
                        {"raw-source-generation"}, {9L}, {"Planned"},
                        {DateTime.UtcNow}, {DateTime.UtcNow});
                    """);
            }

            await using var currentVerification = new ListenArrDbContext(options);
            var current = await currentVerification.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == currentOperationId);
            Assert.Equal(FileMutationProtocol.Current, current.ProtocolVersion);
            Assert.Equal(
                "source-parent-generation",
                current.SourceParentDirectoryObjectIdentity);
            Assert.Equal(
                "destination-parent-generation",
                current.DestinationParentDirectoryObjectIdentity);

            var rawFallback = await currentVerification.FileMutationJournals
                .AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == rawFallbackOperationId);
            Assert.Equal(
                FileMutationProtocol.MarkerlessDatabaseState,
                rawFallback.ProtocolVersion);
            Assert.Equal(
                "raw-source-parent-generation",
                rawFallback.SourceParentDirectoryObjectIdentity);
            Assert.Equal(
                "raw-destination-parent-generation",
                rawFallback.DestinationParentDirectoryObjectIdentity);
            Assert.Empty(await currentVerification.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task PreviousReleasedSchema_UpgradesToCurrentModel()
    {
        var databasePath = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"released-upgrade-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;

        try
        {
            await using (var db = new ListenArrDbContext(options))
            {
                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync("20260619192820_AddHistoryExternalAudiobookId");
                await migrator.MigrateAsync();
            }

            await using var verified = new ListenArrDbContext(options);
            Assert.Empty(await verified.Database.GetPendingMigrationsAsync());
            var columns = await verified.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('ApplicationSettings')")
                .ToListAsync();
            Assert.Contains("Version", columns);
            var indexes = await verified.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index'")
                .ToListAsync();
            Assert.Contains("IX_MoveJobs_ActiveDeduplicationKey", indexes);
            Assert.Contains("IX_DownloadProcessingJobs_ActiveDeduplicationKey", indexes);
            Assert.Contains("IX_Downloads_ActiveAudiobookDeduplicationKey", indexes);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }
}
