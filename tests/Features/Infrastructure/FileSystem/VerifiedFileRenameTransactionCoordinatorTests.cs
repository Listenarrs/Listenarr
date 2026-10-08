using System.Security.Cryptography;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "VerifiedFileRenameTransactionCoordinatorTests")]
[Trait("Category", "Infrastructure")]
public sealed class VerifiedFileRenameTransactionCoordinatorTests : BaseTests
{
    [Fact]
    public async Task PrepareCommitAndRetire_LargeBatch_BoundsHandles()
    {
        var scenario = await CreateScenarioAsync();
        var coordinator = CreateCoordinator();
        const int count = 600;
        var files = new List<AudiobookFile>();
        var members = new List<VerifiedFileRenameBatchMember>();
        for (var index = 0; index < count; index++)
        {
            var source = Path.Join(scenario.Root.Path, $"old-{index:D4}.m4b");
            var destination = Path.Join(scenario.Root.Path, "Author", "Book", $"new-{index:D4}.m4b");
            File.WriteAllText(source, "verified-organize-audio");
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = scenario.Audiobook.Id,
                Path = source,
                Size = scenario.SourceProof.Length,
                Format = "m4b"
            });
            files.Add(file);
            members.Add(new VerifiedFileRenameBatchMember(file.Id, source, destination));
        }
        var manifest = VerifiedFileRenameBatchManifest.Create(members);
        var batchId = Guid.NewGuid();
        var leases = new List<IVerifiedFileRenameLease>();
        void AssertBoundedHandles()
        {
            if (!OperatingSystem.IsLinux()) return;
            var handles = Directory.EnumerateFiles("/proc/self/fd")
                .Count(path => new FileInfo(path).LinkTarget is { } target
                    && target.StartsWith(scenario.Root.Path + "/", StringComparison.Ordinal));
            Assert.InRange(handles, 0, 48);
        }
        try
        {
            foreach (var member in members)
            {
                var prepared = await coordinator.PrepareAsync(member.SourcePath, member.DestinationPath,
                    Guid.NewGuid(), batchId, manifest, scenario.Audiobook.Id,
                    member.AudiobookFileId, scenario.SourceProof);
                Assert.True(prepared.Success, prepared.Error);
                leases.Add(prepared.Lease!);
                AssertBoundedHandles();
            }
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var ids = files.Select(file => file.Id).ToArray();
                var tracked = await db.AudiobookFiles.Where(file => ids.Contains(file.Id)).ToListAsync();
                foreach (var file in tracked)
                    file.Path = members.Single(member => member.AudiobookFileId == file.Id).DestinationPath;
                var store = new FileRenameCommitStore(db, TimeProvider.System)
                {
                    AfterSaveBeforeTargetRevalidationForTest = AssertBoundedHandles
                };
                await store.CommitOwnerMetadataAsync(scenario.Audiobook.Id,
                    leases.Select(lease => lease.OperationId).ToArray());
            }
            foreach (var lease in leases)
            {
                Assert.Equal(VerifiedFileRenameRetirementOutcome.Completed,
                    await lease.CompleteSourceRetirementAsync());
                AssertBoundedHandles();
            }
            Assert.All(members, member =>
            {
                Assert.False(File.Exists(member.SourcePath));
                Assert.Equal("verified-organize-audio", File.ReadAllText(member.DestinationPath));
            });
        }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync();
        }
    }

    [LinuxTheory]
    [InlineData(22)]
    [InlineData(38)]
    [InlineData(95)]
    public async Task PrepareAsync_NoReplaceRenameUnsupported_PublishesPinnedHardlinkAndRollsBack(int nativeError)
    {
        var scenario = await CreateScenarioAsync();
        var coordinator = CreateCoordinator();
        coordinator.PublicationRenameErrorForTest = nativeError;
        var result = await coordinator.PrepareAsync(scenario.Source, scenario.Destination,
            scenario.OperationId, scenario.BatchId, scenario.Manifest, scenario.Audiobook.Id,
            scenario.AudiobookFile.Id, scenario.SourceProof);

        Assert.True(result.Success, result.Error);
        await using var lease = result.Lease!;
        Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Destination));
        Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.False(File.Exists(scenario.StagingPath));
        Assert.True(await lease.RollBackAsync());
        Assert.False(File.Exists(scenario.Destination));
        Assert.Equal(VerifiedFileRenameState.RolledBack, (await GetJournalAsync(scenario.OperationId)).State);
    }

    [LinuxFact]
    public async Task PrepareAsync_FallbackDestinationOccupied_PreservesForeignFileAndSource()
    {
        var scenario = await CreateScenarioAsync();
        var coordinator = CreateCoordinator();
        coordinator.PublicationRenameErrorForTest = 22;
        coordinator.BeforeFallbackPublicationForTest = () => File.WriteAllText(scenario.Destination, "foreign");
        var result = await coordinator.PrepareAsync(scenario.Source, scenario.Destination,
            scenario.OperationId, scenario.BatchId, scenario.Manifest, scenario.Audiobook.Id,
            scenario.AudiobookFile.Id, scenario.SourceProof);

        Assert.False(result.Success);
        Assert.Null(result.Lease);
        Assert.Equal("foreign", await File.ReadAllTextAsync(scenario.Destination));
        Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.False(File.Exists(scenario.StagingPath));
    }

    [LinuxFact]
    public async Task PrepareAsync_InterruptedAfterFallbackLink_RestartRetainsBothNamesAndSource()
    {
        var scenario = await CreateScenarioAsync();
        var coordinator = CreateCoordinator();
        coordinator.PublicationRenameErrorForTest = 22;
        coordinator.AfterFallbackPublicationForTest = () => throw new OperationCanceledException("Injected interruption after link.");
        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.PrepareAsync(
            scenario.Source, scenario.Destination, scenario.OperationId, scenario.BatchId,
            scenario.Manifest, scenario.Audiobook.Id, scenario.AudiobookFile.Id, scenario.SourceProof));
        var recovery = _provider.GetRequiredService<IVerifiedFileRenameRecoveryService>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await recovery.ReconcileAsync();
            Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Source));
            Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Destination));
            Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.StagingPath));
            Assert.Equal(VerifiedFileRenameState.NeedsAttention, (await GetJournalAsync(scenario.OperationId)).State);
        }
    }

    [LinuxTheory]
    [InlineData(13)]
    [InlineData(17)]
    public async Task PrepareAsync_RenameDeniedOrCollision_DoesNotAttemptFallback(int nativeError)
    {
        var scenario = await CreateScenarioAsync();
        var coordinator = CreateCoordinator();
        coordinator.PublicationRenameErrorForTest = nativeError;
        var fallbackAttempted = false;
        coordinator.BeforeFallbackPublicationForTest = () => fallbackAttempted = true;
        var result = await coordinator.PrepareAsync(scenario.Source, scenario.Destination,
            scenario.OperationId, scenario.BatchId, scenario.Manifest, scenario.Audiobook.Id,
            scenario.AudiobookFile.Id, scenario.SourceProof);
        Assert.False(result.Success);
        Assert.False(fallbackAttempted);
        Assert.False(File.Exists(scenario.Destination));
        Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal(VerifiedFileRenameState.RolledBack, (await GetJournalAsync(scenario.OperationId)).State);
    }

    [LinuxFact]
    public async Task PrepareAsync_FallbackStagingReplaced_DoesNotDeleteReplacement()
    {
        var scenario = await CreateScenarioAsync();
        var coordinator = CreateCoordinator();
        coordinator.PublicationRenameErrorForTest = 22;
        coordinator.AfterFallbackPublicationForTest = () =>
        {
            File.Move(scenario.StagingPath, scenario.StagingPath + ".saved");
            File.WriteAllText(scenario.StagingPath, "foreign staging");
        };
        var result = await coordinator.PrepareAsync(scenario.Source, scenario.Destination,
            scenario.OperationId, scenario.BatchId, scenario.Manifest, scenario.Audiobook.Id,
            scenario.AudiobookFile.Id, scenario.SourceProof);
        Assert.False(result.Success);
        Assert.Equal("foreign staging", await File.ReadAllTextAsync(scenario.StagingPath));
        Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.StagingPath + ".saved"));
        Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_UnresolvedNestedConfiguredRoot_DoesNotBorrowOuterBoundary(bool paddedRoot)
    {
        var scenario = await CreateScenarioAsync();
        var nested = Directory.CreateDirectory(Path.Join(scenario.Root.Path, "Author")).FullName;
        await _rootFolderRepository.AddAsync(new RootFolder
        {
            Name = "Unresolved nested root",
            Path = paddedRoot ? " " + nested + " " : nested,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            PathIdentityState = PathIdentityState.Unavailable,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown,
        });
        var result = await CreateCoordinator().PrepareAsync(scenario.Source, scenario.Destination,
            scenario.OperationId, scenario.BatchId, scenario.Manifest, scenario.Audiobook.Id,
            scenario.AudiobookFile.Id, scenario.SourceProof);
        Assert.False(result.Success);
        Assert.Null(result.Lease);
        Assert.Contains("unresolved configured boundary", result.Error);
        Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
    }

    [DirectoryLinkTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedConfiguredBoundary_PrepareCommitAndFinish_UsesLiveBoundaryAndRestartRetainsSource(bool restart)
    {
        var scenario = await CreateLinkedScenarioAsync();
        var preparation = await CreateCoordinator().PrepareAsync(scenario.Source, scenario.Destination,
            scenario.OperationId, scenario.BatchId, scenario.Manifest, scenario.Audiobook.Id,
            scenario.AudiobookFile.Id, scenario.SourceProof);
        Assert.True(preparation.Success, preparation.Error);
        Assert.NotNull(preparation.Lease);
        await using var lease = preparation.Lease!;
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var file = await db.AudiobookFiles.SingleAsync(candidate => candidate.Id == scenario.AudiobookFile.Id);
            var identity = await _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>()
                .ResolveAsync(scenario.Audiobook, scenario.Destination);
            Assert.Equal(PathIdentityState.Valid, identity.State);
            file.ApplyPathIdentity(scenario.Destination, identity);
            await new FileRenameCommitStore(db, TimeProvider.System)
                .CommitOwnerMetadataAsync(scenario.Audiobook.Id, [scenario.OperationId]);
        }
        if (restart)
        {
            await lease.DisposeAsync();
            var recovery = _provider.GetRequiredService<IVerifiedFileRenameRecoveryService>();
            await recovery.ReconcileAsync();
            await recovery.ReconcileAsync();
        }
        else
        {
            Assert.Equal(VerifiedFileRenameRetirementOutcome.Completed, await lease.CompleteSourceRetirementAsync());
            await lease.DisposeAsync();
        }
        Assert.Equal(restart, File.Exists(scenario.Source));
        Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Destination));
        Assert.Equal(restart ? VerifiedFileRenameState.CompletedSourceRetained : VerifiedFileRenameState.Completed,
            (await GetJournalAsync(scenario.OperationId)).State);
        Assert.True(Directory.Exists(scenario.Root.Path));
    }

    [Fact]
    public void ResolveDestinationHierarchySegments_CaseInsensitiveUnixAlias_DoesNotTraverseAboveRoot()
    {
        var segments = VerifiedFileRenameTransactionCoordinator
            .ResolveDestinationHierarchySegments(
                "/mnt/Library",
                "/mnt/library/Author/Book",
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Unix,
                    FileSystemCaseSensitivity.Insensitive));

        Assert.Equal(["Author", "Book"], segments);
        Assert.DoesNotContain("..", segments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrepareAsync_ContentOnlySource_PublishesTargetWithoutRetiringSource_AndRollsBackExactly(bool managedRoot)
    {
        var scenario = await CreateScenarioAsync();
        if (!managedRoot)
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            db.RootFolders.Remove(await db.RootFolders.SingleAsync(root => root.Id == scenario.Root.Id));
            await db.SaveChangesAsync();
        }
        var coordinator = CreateCoordinator();
        var preparation = await coordinator.PrepareAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            scenario.BatchId,
            scenario.Manifest,
            scenario.Audiobook.Id,
            scenario.AudiobookFile.Id,
            scenario.SourceProof);

        Assert.True(preparation.Success, preparation.Error);
        Assert.NotNull(preparation.Lease);
        Assert.True(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        Assert.False(File.Exists(scenario.StagingPath));
        Assert.Equal(
            VerifiedFileRenameState.TargetVerified,
            (await GetJournalAsync(scenario.OperationId)).State);

        Assert.True(await preparation.Lease!.RollBackAsync());
        await preparation.Lease.DisposeAsync();
        Assert.True(File.Exists(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        Assert.Equal(
            VerifiedFileRenameState.RolledBack,
            (await GetJournalAsync(scenario.OperationId)).State);
    }

    [Fact]
    public async Task CompleteSourceRetirementAsync_AfterOwnerCommit_DeletesOnlyLivePinnedSource()
    {
        var scenario = await CreateScenarioAsync();
        var coordinator = CreateCoordinator();
        var preparation = await coordinator.PrepareAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            scenario.BatchId,
            scenario.Manifest,
            scenario.Audiobook.Id,
            scenario.AudiobookFile.Id,
            scenario.SourceProof);
        Assert.True(preparation.Success, preparation.Error);

        await SetJournalStateAsync(
            scenario.OperationId,
            VerifiedFileRenameState.OwnerMetadataReconciled);

        Assert.Equal(
            VerifiedFileRenameRetirementOutcome.Completed,
            await preparation.Lease!.CompleteSourceRetirementAsync());
        await preparation.Lease.DisposeAsync();
        Assert.False(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        Assert.Equal(
            VerifiedFileRenameState.Completed,
            (await GetJournalAsync(scenario.OperationId)).State);
    }

    [Theory]
    [InlineData(false, VerifiedFileRenameState.RolledBack)]
    [InlineData(true, VerifiedFileRenameState.NeedsAttention)]
    public async Task PrepareAsync_InterruptedBeforeTargetVerification_RestartPreservesContentAndOwner(
        bool afterPublication,
        VerifiedFileRenameState expectedRecoveryState)
    {
        var scenario = await CreateScenarioAsync();
        var identityResolver = _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>();
        var sourceIdentity = await identityResolver.ResolveAsync(scenario.Audiobook, scenario.Source);
        Assert.Equal(PathIdentityState.Valid, sourceIdentity.State);
        await using (var db = await _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContextAsync())
        {
            var file = await db.AudiobookFiles.SingleAsync(file => file.Id == scenario.AudiobookFile.Id);
            file.ApplyPathIdentity(scenario.Source, sourceIdentity);
            await db.SaveChangesAsync();
        }
        var coordinator = CreateCoordinator();
        Action interrupt = () => throw new OperationCanceledException("Injected publication interruption.");
        if (afterPublication)
        {
            coordinator.AfterTargetPublicationForTest = interrupt;
        }
        else
        {
            coordinator.AfterJournalPlannedForTest = interrupt;
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.PrepareAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            scenario.BatchId,
            scenario.Manifest,
            scenario.Audiobook.Id,
            scenario.AudiobookFile.Id,
            scenario.SourceProof));

        Assert.Equal(
            VerifiedFileRenameState.Planned,
            (await GetJournalAsync(scenario.OperationId)).State);
        var recovery = new VerifiedFileRenameRecoveryService(
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>(),
            TimeProvider.System,
            NullLogger<VerifiedFileRenameRecoveryService>.Instance);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await recovery.ReconcileAsync();

            Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Source));
            Assert.Equal(afterPublication, File.Exists(scenario.Destination));
            if (afterPublication)
            {
                Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Destination));
            }
            Assert.False(File.Exists(scenario.StagingPath));
            Assert.False(File.Exists(scenario.RetirementPath));
            Assert.Equal(expectedRecoveryState, (await GetJournalAsync(scenario.OperationId)).State);
            await using var db = await _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync();
            Assert.Equal(scenario.Source, (await db.AudiobookFiles.AsNoTracking()
                .SingleAsync(file => file.Id == scenario.AudiobookFile.Id)).Path);
        }
    }

    [LinuxFact]
    public async Task CompleteSourceRetirementAsync_TargetReplacedAfterSourceQuarantine_RestoresExactSource()
    {
        var scenario = await CreateScenarioAsync();
        var coordinator = CreateCoordinator();
        var preparation = await coordinator.PrepareAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            scenario.BatchId,
            scenario.Manifest,
            scenario.Audiobook.Id,
            scenario.AudiobookFile.Id,
            scenario.SourceProof);
        Assert.True(preparation.Success, preparation.Error);
        await SetJournalStateAsync(
            scenario.OperationId,
            VerifiedFileRenameState.OwnerMetadataReconciled);
        coordinator.AfterSourceQuarantinedForTest = () =>
        {
            File.Delete(scenario.Destination);
            File.WriteAllText(scenario.Destination, "foreign-target");
        };

        Assert.Equal(
            VerifiedFileRenameRetirementOutcome.NeedsAttention,
            await preparation.Lease!.CompleteSourceRetirementAsync());
        await preparation.Lease.DisposeAsync();

        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("foreign-target", await File.ReadAllTextAsync(scenario.Destination));
        Assert.False(File.Exists(scenario.RetirementPath));
        var journal = await GetJournalAsync(scenario.OperationId);
        Assert.Equal(VerifiedFileRenameState.NeedsAttention, journal.State);
        Assert.Contains("requires repair", journal.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteSourceRetirementAsync_StorageContractChanged_RetainsSourceTerminally()
    {
        var scenario = await CreateScenarioAsync();
        var coordinator = CreateCoordinator();
        var preparation = await coordinator.PrepareAsync(
            scenario.Source,
            scenario.Destination,
            scenario.OperationId,
            scenario.BatchId,
            scenario.Manifest,
            scenario.Audiobook.Id,
            scenario.AudiobookFile.Id,
            scenario.SourceProof);
        Assert.True(preparation.Success, preparation.Error);
        await SetJournalStateAsync(
            scenario.OperationId,
            VerifiedFileRenameState.OwnerMetadataReconciled);

        scenario.Root.StorageContractRevision++;
        await _rootFolderRepository.UpdateAsync(scenario.Root);

        Assert.Equal(
            VerifiedFileRenameRetirementOutcome.SourceRetained,
            await preparation.Lease!.CompleteSourceRetirementAsync());
        Assert.True(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        var journal = await GetJournalAsync(scenario.OperationId);
        Assert.Equal(VerifiedFileRenameState.CompletedSourceRetained, journal.State);
        Assert.Contains("source was retained", journal.Error, StringComparison.OrdinalIgnoreCase);
        await preparation.Lease.DisposeAsync();
    }

    private VerifiedFileRenameTransactionCoordinator CreateCoordinator()
    {
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<RootFolder>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RootFolderStorageObservation(
                RootFolderStorageState.Limited,
                RootFolderStorageReason.IdentityUnsupported,
                Message: null,
                CanConfirmCurrentFolder: false,
                CanChangePath: true,
                CanMutateFilesystem: false,
                ConfirmationToken: null,
                CanPublishNewFiles: true,
                CanRetireSource: false,
                CanRetireAfterVerifiedCopy: true));
        return new VerifiedFileRenameTransactionCoordinator(
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            _rootFolderRepository,
            health.Object,
            TimeProvider.System,
            NullLogger<VerifiedFileRenameTransactionCoordinator>.Instance);
    }

    [DirectoryLinkFact]
    public async Task PrepareAsync_IntermediateSourceLink_PreservesForeignContentWithoutPublishing()
    {
        var scenario = await CreateScenarioAsync();
        var foreign = FileService.GetTempDirectory("verified-organize-foreign");
        var foreignBook = Directory.CreateDirectory(Path.Join(foreign, "Book")).FullName;
        var foreignSource = Path.Join(foreignBook, "old.m4b");
        File.Copy(scenario.Source, foreignSource);
        var ancestorLink = Path.Join(scenario.Root.Path, "LinkedAuthor");
        Directory.CreateSymbolicLink(ancestorLink, foreign);
        var linkedSource = Path.Join(ancestorLink, "Book", "old.m4b");
        var manifest = VerifiedFileRenameBatchManifest.Create(
        [
            new VerifiedFileRenameBatchMember(
                scenario.AudiobookFile.Id, linkedSource, scenario.Destination)
        ]);
        try
        {
            var preparation = await CreateCoordinator().PrepareAsync(
                linkedSource,
                scenario.Destination,
                scenario.OperationId,
                scenario.BatchId,
                manifest,
                scenario.Audiobook.Id,
                scenario.AudiobookFile.Id,
                scenario.SourceProof);

            Assert.False(preparation.Success);
            Assert.Null(preparation.Lease);
            Assert.False(File.Exists(scenario.Destination));
            Assert.Equal("verified-organize-audio", await File.ReadAllTextAsync(foreignSource));
            Assert.True(File.Exists(scenario.Source));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            Assert.False(await db.VerifiedFileRenameJournals.AnyAsync(
                journal => journal.OperationId == scenario.OperationId));
        }
        finally
        {
            Directory.Delete(ancestorLink);
        }
    }

    private Task<Scenario> CreateLinkedScenarioAsync()
    {
        var directory = FileService.GetTempDirectory("verified-organize-coordinator-linked");
        var physical = Directory.CreateDirectory(Path.Join(directory, "physical")).FullName;
        var linked = Path.Join(directory, "linked");
        Directory.CreateSymbolicLink(linked, physical);
        return CreateScenarioAsync(linked);
    }

    private async Task<Scenario> CreateScenarioAsync(string? configuredRootPath = null)
    {
        var rootPath = configuredRootPath
            ?? FileService.GetTempDirectory("verified-organize-coordinator");
        var root = await AddAuthorizedRootAsync(rootPath);
        root.StorageContractRevision = 11;
        await _rootFolderRepository.UpdateAsync(root);

        var source = Path.Join(rootPath, "old.m4b");
        var destination = Path.Join(rootPath, "Author", "Book", "new.m4b");
        await File.WriteAllTextAsync(source, "verified-organize-audio");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Verified Organize",
            BasePath = rootPath
        });
        var audiobookFile = await _audiobookFileRepository.AddAsync(new AudiobookFile
        {
            AudiobookId = audiobook.Id,
            Path = source,
            Size = new FileInfo(source).Length,
            Format = "m4b"
        });
        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var manifest = VerifiedFileRenameBatchManifest.Create(
        [
            new VerifiedFileRenameBatchMember(
                audiobookFile.Id,
                source,
                destination)
        ]);
        var bytes = await File.ReadAllBytesAsync(source);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var sourceProof = new FilePublicationSourceProof(
            "content-only:" + sha256,
            bytes.LongLength,
            sha256,
            FilePublicationSourceAuthority.ContentOnly);
        var stagingPath = Path.Join(
            Path.GetDirectoryName(destination)!,
            ".listenarr-organize-" + operationId.ToString("N") + ".partial");
        var retirementPath = Path.Join(
            Path.GetDirectoryName(source)!,
            ".listenarr-organize-" + operationId.ToString("N") + ".source");
        return new Scenario(
            root,
            audiobook,
            audiobookFile,
            source,
            destination,
            stagingPath,
            retirementPath,
            operationId,
            batchId,
            manifest,
            sourceProof);
    }

    private async Task<VerifiedFileRenameJournal> GetJournalAsync(Guid operationId)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.VerifiedFileRenameJournals
            .AsNoTracking()
            .SingleAsync(journal => journal.OperationId == operationId);
    }

    private async Task SetJournalStateAsync(
        Guid operationId,
        VerifiedFileRenameState state)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.VerifiedFileRenameJournals
            .SingleAsync(candidate => candidate.OperationId == operationId);
        journal.State = state;
        await db.SaveChangesAsync();
    }

    private sealed record Scenario(
        RootFolder Root,
        Audiobook Audiobook,
        AudiobookFile AudiobookFile,
        string Source,
        string Destination,
        string StagingPath,
        string RetirementPath,
        Guid OperationId,
        Guid BatchId,
        VerifiedFileRenameBatchManifest Manifest,
        FilePublicationSourceProof SourceProof);
}
