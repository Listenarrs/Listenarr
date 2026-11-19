/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

[Trait("Name", "ReleasedJournalRecoveryUpgradeTests")]
[Trait("Category", "Integration")]
public sealed class ReleasedJournalRecoveryUpgradeTests : BaseTests
{
    public static IEnumerable<object[]> ReleasedJournalStates()
    {
        // These states and protocols exist in the v1.3.4-canary release.
        FileMutationJournalState[] states =
        [
            FileMutationJournalState.Planned,
            FileMutationJournalState.TargetIdentityPersisted,
            FileMutationJournalState.TargetVerified,
            FileMutationJournalState.RegistrationCommitted,
            FileMutationJournalState.SourceDeletionAuthorized,
            FileMutationJournalState.SourceDeleted,
            FileMutationJournalState.OwnerMetadataReconciled,
            FileMutationJournalState.NeedsAttention,
            FileMutationJournalState.Completed
        ];
        foreach (var protocol in new[] { 1, 2 })
        {
            foreach (var state in states)
            {
                yield return [protocol, state, 0];
                yield return [protocol, state, 1];
                if (state == FileMutationJournalState.TargetVerified)
                {
                    yield return [protocol, state, 2];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ReleasedJournalStates))]
    public async Task Released134Journal_UpgradeAndRepeatedRestart_PreserveBothArtifacts(
        int protocol,
        FileMutationJournalState state,
        int ownerMode)
    {
        // This test deliberately uses a migrated SQLite database rather than the
        // test host's current-model database, so old rows cross the upgrade boundary.
        var root = Path.Join(Path.GetTempPath(), $"released-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Join(root, "listenarr.db");
        var source = Path.Join(root, "source.m4b");
        var target = Path.Join(root, "target.m4b");
        byte[] content = [0, 1, 2, 127, 128, 255];
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        var operationId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var factory = new UpgradeDbContextFactory(options);
        var audiobook = new AudiobookBuilder()
            .WithTitle("Released publication owner")
            .WithBasePath(root)
            .WithFilePath(target)
            .Build();
        var file = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(target)
            .WithSize(content.LongLength)
            .Build();
        file.ApplyPathIdentity(target, AudiobookFilePathIdentity.CreateValid(
            target,
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemCaseSensitivityMode.Auto,
            root));
        audiobook.Files = [file];
        int? journalOwnerId = ownerMode == 1 ? audiobook.Id : null;

        try
        {
            await File.WriteAllBytesAsync(source, content);
            await File.WriteAllBytesAsync(target, content);
            await using (var legacy = await factory.CreateDbContextAsync())
            {
                var migrator = legacy.GetService<IMigrator>();
                await migrator.MigrateAsync("20260825021432_AddWeakStorageVerifiedCleanup");
                if (ownerMode != 0)
                {
                    legacy.Audiobooks.Add(audiobook);
                    await legacy.SaveChangesAsync();
                }
                await legacy.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "FileMutationJournals" (
                        "OperationId", "ProtocolVersion", "Action", "SourcePath", "DestinationPath",
                        "SourceParentDirectoryObjectIdentity", "DestinationParentDirectoryObjectIdentity",
                        "SourcePhysicalObjectIdentity", "TargetPhysicalObjectIdentity",
                        "SourceLength", "SourceSha256", "State", "AudiobookId", "CreatedAt", "UpdatedAt")
                    VALUES (
                        {operationId}, {protocol}, {"Move"}, {source}, {target},
                        {"released-source-parent"}, {"released-target-parent"},
                        {"released-source-object"}, {"released-target-object"},
                        {content.LongLength}, {hash}, {state.ToString()}, {journalOwnerId}, {DateTime.UtcNow}, {DateTime.UtcNow});
                    """);
                await migrator.MigrateAsync();
            }

            FileMutationJournalState? firstRecoveredState = null;
            for (var restart = 0; restart < 2; restart++)
            {
                var mover = new Mock<IFileMover>(MockBehavior.Strict);
                var recovery = new FileRegistrationRecoveryService(
                    factory, mover.Object, TimeProvider.System,
                    NullLogger<FileRegistrationRecoveryService>.Instance);
                await recovery.ReconcileAsync();

                Assert.Equal(content, await File.ReadAllBytesAsync(source));
                Assert.Equal(content, await File.ReadAllBytesAsync(target));
                mover.VerifyNoOtherCalls();
                await using var verified = await factory.CreateDbContextAsync();
                Assert.Empty(await verified.Database.GetPendingMigrationsAsync());
                var journal = await verified.FileMutationJournals.AsNoTracking()
                    .SingleAsync(candidate => candidate.OperationId == operationId);
                Assert.Equal(protocol, journal.ProtocolVersion);
                Assert.Equal(hash, journal.SourceSha256);
                Assert.Equal("released-source-object", journal.SourcePhysicalObjectIdentity);
                if (ownerMode != 0)
                {
                    var storedFile = await verified.AudiobookFiles.AsNoTracking().SingleAsync();
                    Assert.Equal(file.Id, storedFile.Id);
                    Assert.Equal(audiobook.Id, storedFile.AudiobookId);
                    Assert.Equal(target, storedFile.Path);
                    Assert.Equal(file.CanonicalPath, storedFile.CanonicalPath);
                    Assert.Equal(file.PathOwnershipKey, storedFile.PathOwnershipKey);
                    Assert.Equal(target, (await verified.Audiobooks.AsNoTracking().SingleAsync()).FilePath);
                    Assert.Equal(audiobook.Id, journal.AudiobookId);
                    if (state is FileMutationJournalState.TargetVerified
                        or FileMutationJournalState.RegistrationCommitted
                        or FileMutationJournalState.SourceDeletionAuthorized
                        or FileMutationJournalState.SourceDeleted)
                    {
                        Assert.Equal(FileMutationJournalState.CompletedSourceRetained, journal.State);
                    }
                }
                else
                {
                    Assert.Null(journal.AudiobookId);
                }
                if (state == FileMutationJournalState.Completed)
                {
                    Assert.Equal(state, journal.State);
                }
                if (firstRecoveredState.HasValue)
                {
                    Assert.Equal(firstRecoveredState.Value, journal.State);
                }
                firstRecoveredState = journal.State;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class UpgradeDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListenArrDbContext(options));
    }
}
