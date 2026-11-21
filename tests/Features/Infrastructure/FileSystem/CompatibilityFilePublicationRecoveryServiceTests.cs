using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "CompatibilityFilePublicationRecoveryServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class CompatibilityFilePublicationRecoveryServiceTests : BaseTests
{
    [DirectoryLinkTheory]
    [InlineData(CompatibilityFilePublicationState.TargetVerified, false)]
    [InlineData(CompatibilityFilePublicationState.RegistrationCommitted, false)]
    [InlineData(CompatibilityFilePublicationState.SourceQuarantined, false)]
    [InlineData(CompatibilityFilePublicationState.TargetVerified, true)]
    [InlineData(CompatibilityFilePublicationState.RegistrationCommitted, true)]
    [InlineData(CompatibilityFilePublicationState.SourceQuarantined, true)]
    public async Task ReconcileAsync_ConfiguredLinkedTargetBoundary_AllowsOnlyBoundaryLink(
        CompatibilityFilePublicationState state, bool linkedDescendant)
    {
        var directory = FileService.GetTempDirectory("compatibility-configured-linked-target");
        var physical = Directory.CreateDirectory(Path.Join(directory, "physical")).FullName;
        var linked = Path.Join(directory, "library");
        Directory.CreateSymbolicLink(linked, physical);
        var foreign = Directory.CreateDirectory(Path.Join(directory, "foreign")).FullName;
        var configured = await AddAuthorizedRootAsync(linked, "Linked compatibility target");
        var targetParent = Path.Join(linked, "Book");
        if (linkedDescendant) Directory.CreateSymbolicLink(targetParent, foreign);
        else Directory.CreateDirectory(targetParent);
        var journal = new CompatibilityQuarantineJournalBuilder(directory).Build();
        journal.DestinationPath = Path.Join(targetParent, "destination.m4b");
        journal.DestinationRootFolderId = configured.Id;
        journal.DestinationStorageContractRevision = configured.StorageContractRevision;
        journal.State = state;
        await File.WriteAllTextAsync(journal.SourcePath, "audio");
        await File.WriteAllTextAsync(journal.DestinationPath, "audio");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CompatibilityFilePublicationJournals.Add(journal);
            await db.SaveChangesAsync();
        }

        await CreateRecoveryService(factory).ReconcileAsync();
        await CreateRecoveryService(factory).ReconcileAsync();

        await using var verification = await factory.CreateDbContextAsync();
        var recovered = await verification.CompatibilityFilePublicationJournals.SingleAsync();
        Assert.Equal(linkedDescendant ? CompatibilityFilePublicationState.NeedsAttention
            : state == CompatibilityFilePublicationState.TargetVerified ? state
            : CompatibilityFilePublicationState.Completed, recovered.State);
        Assert.Equal("audio", await File.ReadAllTextAsync(journal.SourcePath));
        Assert.Equal("audio", await File.ReadAllTextAsync(journal.DestinationPath));
        Assert.Equal(physical, new DirectoryInfo(linked).LinkTarget);
    }

    [FileLinkTheory]
    [InlineData(CompatibilityFilePublicationState.TargetVerified)]
    [InlineData(CompatibilityFilePublicationState.RegistrationCommitted)]
    [InlineData(CompatibilityFilePublicationState.SourceQuarantined)]
    public Task ReconcileAsync_LinkedDestination_DoesNotAdoptForeignContent(
        CompatibilityFilePublicationState state) =>
        AssertLinkedDestinationIsRetainedAsync(state, linkParent: false,
            (journal, _, foreign) => File.CreateSymbolicLink(journal.DestinationPath, foreign));

    [DirectoryLinkTheory]
    [InlineData(CompatibilityFilePublicationState.TargetVerified)]
    [InlineData(CompatibilityFilePublicationState.RegistrationCommitted)]
    [InlineData(CompatibilityFilePublicationState.SourceQuarantined)]
    public Task ReconcileAsync_LinkedDestinationParent_DoesNotAdoptForeignContent(
        CompatibilityFilePublicationState state) =>
        AssertLinkedDestinationIsRetainedAsync(state, linkParent: true,
            (journal, root, foreign) =>
            {
                var parent = Path.Join(root, "linked-parent");
                Directory.CreateSymbolicLink(parent, Path.GetDirectoryName(foreign)!);
                journal.DestinationPath = Path.Join(parent, "destination.m4b");
            });

    private async Task AssertLinkedDestinationIsRetainedAsync(
        CompatibilityFilePublicationState state,
        bool linkParent,
        Action<CompatibilityFilePublicationJournal, string, string> createLink)
    {
        // Given: persisted publication bytes match a foreign file reached through a link.
        Init();
        var root = FileService.GetTempDirectory("compatibility-linked-destination");
        var journal = new CompatibilityQuarantineJournalBuilder(root).Build();
        journal.State = state;
        var foreignDirectory = Path.Join(root, "foreign");
        Directory.CreateDirectory(foreignDirectory);
        var foreign = Path.Join(foreignDirectory, "destination.m4b");
        await File.WriteAllTextAsync(foreign, "audio");
        await File.WriteAllTextAsync(journal.SourcePath, "audio");
        createLink(journal, root, foreign);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CompatibilityFilePublicationJournals.Add(journal);
            await db.SaveChangesAsync();
        }
        var recovery = CreateRecoveryService(factory);

        // When: restart recovery is repeated.
        await recovery.ReconcileAsync();
        await recovery.ReconcileAsync();

        // Then: recovery preserves both artifacts and rejects linked publication evidence.
        Assert.Equal("audio", await File.ReadAllTextAsync(journal.SourcePath));
        Assert.Equal("audio", await File.ReadAllTextAsync(foreign));
        Assert.Equal(linkParent ? foreignDirectory : foreign,
            linkParent ? new DirectoryInfo(Path.GetDirectoryName(journal.DestinationPath)!).LinkTarget
                : new FileInfo(journal.DestinationPath).LinkTarget);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(CompatibilityFilePublicationState.NeedsAttention,
            (await verification.CompatibilityFilePublicationJournals.SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_QuarantineRestore_VerifiesTheActuallyPinnedSource(
        bool replaceBeforeRestore)
    {
        Init();
        var root = FileService.GetTempDirectory("compatibility-quarantine-substitution");
        var journal = new CompatibilityQuarantineJournalBuilder(root).Build();
        Directory.CreateDirectory(Path.GetDirectoryName(journal.QuarantinePath!)!);
        await File.WriteAllTextAsync(journal.QuarantinePath!, "audio");
        await File.WriteAllTextAsync(journal.DestinationPath, "audio");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CompatibilityFilePublicationJournals.Add(journal);
            await db.SaveChangesAsync();
        }
        var service = CreateRecoveryService(factory);
        var displaced = journal.QuarantinePath + ".original";
        if (replaceBeforeRestore)
        {
            service.BeforeQuarantineRestoreForTest = () =>
            {
                File.Move(journal.QuarantinePath!, displaced);
                File.WriteAllText(journal.QuarantinePath!, "other");
            };
        }

        await service.ReconcileAsync();
        await service.ReconcileAsync();

        if (replaceBeforeRestore)
        {
            Assert.False(File.Exists(journal.SourcePath));
            Assert.Equal("audio", await File.ReadAllTextAsync(displaced));
            Assert.Equal("other", await File.ReadAllTextAsync(journal.QuarantinePath!));
        }
        else
        {
            Assert.Equal("audio", await File.ReadAllTextAsync(journal.SourcePath));
            Assert.False(File.Exists(journal.QuarantinePath));
        }
        Assert.Equal("audio", await File.ReadAllTextAsync(journal.DestinationPath));
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(replaceBeforeRestore ? CompatibilityFilePublicationState.NeedsAttention
            : CompatibilityFilePublicationState.Completed,
            (await verification.CompatibilityFilePublicationJournals.SingleAsync()).State);
    }


    [Fact]
    public async Task ReconcileAsync_PlannedJournalWithTarget_PreservesBothAndMarksAttention()
    {
        var root = FileService.GetTempDirectory("compatibility-recovery-target");
        var source = Path.Join(root, "source.m4b");
        var destination = Path.Join(root, "destination.m4b");
        await File.WriteAllTextAsync(source, "audio");
        await File.WriteAllTextAsync(destination, "partial");
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CompatibilityFilePublicationJournals.Add(
                new CompatibilityFilePublicationJournal
                {
                    OperationId = operationId,
                    RequestedAction = FileAction.Move,
                    EffectiveAction = FileAction.Copy,
                    SourcePath = source,
                    DestinationPath = destination,
                    SourceLength = 5,
                    SourceSha256 = new string('A', 64),
                    State = CompatibilityFilePublicationState.Planned
                });
            await db.SaveChangesAsync();
        }
        var service = CreateRecoveryService(factory);

        await service.ReconcileAsync();

        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("partial", await File.ReadAllTextAsync(destination));
        await using var verification = await factory.CreateDbContextAsync();
        var journal = await verification.CompatibilityFilePublicationJournals
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(
            CompatibilityFilePublicationState.NeedsAttention,
            journal.State);
    }

    [Fact]
    public async Task ReconcileAsync_CompleteManifestedDownloadClientBatch_RetainsSourcesAfterRestart()
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        var scenario = await CreateCommittedCompanionBatchAsync(
            factory,
            includeManifest: true);
        var service = CreateRecoveryService(factory);

        await service.ReconcileAsync();

        await using var verification = await factory.CreateDbContextAsync();
        var journals = await verification.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .Where(journal => journal.BatchId == scenario.BatchId)
            .OrderBy(journal => journal.SourcePath)
            .ToListAsync();
        Assert.Equal(2, journals.Count);
        Assert.All(journals, journal =>
        {
            Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
            Assert.Equal(
                CompatibilitySourceDisposition.Retained,
                journal.SourceDisposition);
        });
        Assert.All(scenario.Sources, source => Assert.True(File.Exists(source)));
    }

    [Fact]
    public async Task LegacyRetainOnlyAttempt_CanRebindToNewStableManifestedBatch()
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        var scenario = await CreateCommittedCompanionBatchAsync(
            factory,
            includeManifest: false);
        var recovery = CreateRecoveryService(factory);

        await recovery.ReconcileAsync();

        CompatibilityFilePublicationJournal legacy;
        await using (var db = await factory.CreateDbContextAsync())
        {
            legacy = await db.CompatibilityFilePublicationJournals
                .AsNoTracking()
                .OrderBy(journal => journal.SourcePath)
                .FirstAsync(journal => journal.BatchId == scenario.BatchId);
        }
        Assert.Equal(CompatibilityFilePublicationState.Completed, legacy.State);
        Assert.Equal(CompatibilitySourceDisposition.Retained, legacy.SourceDisposition);
        Assert.Null(legacy.ExpectedBatchMemberCount);

        var manifest = CompatibilityBatchManifest.Create(scenario.Sources);
        var stableBatchId = Guid.NewGuid();
        var store = new CompatibilityFilePublicationJournalStore(
            factory,
            TimeProvider.System);
        var rebound = await store.GetOrCreateAsync(
            new CompatibilityFilePublicationClaim(
                legacy.OperationId,
                legacy.RequestedAction,
                legacy.SourcePath,
                legacy.DestinationPath,
                legacy.SourceLength,
                legacy.SourceSha256,
                legacy.IsCompanionFile,
                stableBatchId,
                legacy.CleanupOwner,
                legacy.SourceRootFolderId,
                legacy.SourcePolicyRevision,
                legacy.DestinationRootFolderId,
                legacy.DestinationPolicyRevision,
                legacy.SourceStorageContractRevision,
                legacy.DestinationStorageContractRevision,
                manifest.ExpectedMemberCount,
                manifest.SourceManifestSha256),
            CancellationToken.None);

        Assert.Equal(stableBatchId, rebound.BatchId);
        Assert.Equal(
            CompatibilityFilePublicationState.RegistrationCommitted,
            rebound.State);
        Assert.Equal(CompatibilitySourceDisposition.Retained, rebound.SourceDisposition);
        Assert.Equal(manifest.ExpectedMemberCount, rebound.ExpectedBatchMemberCount);
        Assert.Equal(
            manifest.SourceManifestSha256,
            rebound.ExpectedBatchSourceManifestSha256);
        Assert.Equal(legacy.SourcePath, rebound.SourcePath);
        Assert.Equal(legacy.DestinationPath, rebound.DestinationPath);
    }

    [Fact]
    public async Task ReconcileAsync_IncompleteManifestedBatch_RemainsPendingUntilRetryCompletesManifest()
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        var scenario = await CreateCommittedCompanionBatchAsync(
            factory,
            includeManifest: true);
        var missingSource = Path.Join(
            Path.GetDirectoryName(scenario.Sources[0])!,
            "source-3.nfo");
        await File.WriteAllTextAsync(missingSource, "metadata");

        string missingDestination;
        CompatibilityBatchManifest manifest;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var journals = await db.CompatibilityFilePublicationJournals
                .Where(journal => journal.BatchId == scenario.BatchId)
                .ToListAsync();
            var rootId = Assert.IsType<int>(journals[0].DestinationRootFolderId);
            var root = await db.RootFolders.SingleAsync(candidate => candidate.Id == rootId);
            missingDestination = Path.Join(root.Path, "book", "book.nfo");
            await File.WriteAllTextAsync(missingDestination, "metadata");
            manifest = CompatibilityBatchManifest.Create(
                scenario.Sources.Append(missingSource));
            foreach (var journal in journals)
            {
                journal.ExpectedBatchMemberCount = manifest.ExpectedMemberCount;
                journal.ExpectedBatchSourceManifestSha256 = manifest.SourceManifestSha256;
            }
            await db.SaveChangesAsync();
        }

        var service = CreateRecoveryService(factory);
        await service.ReconcileAsync();

        await using (var pending = await factory.CreateDbContextAsync())
        {
            var journals = await pending.CompatibilityFilePublicationJournals
                .AsNoTracking()
                .Where(journal => journal.BatchId == scenario.BatchId)
                .ToListAsync();
            Assert.Equal(2, journals.Count);
            Assert.All(journals, journal =>
            {
                Assert.Equal(
                    CompatibilityFilePublicationState.RegistrationCommitted,
                    journal.State);
                Assert.Equal(
                    CompatibilitySourceDisposition.Retained,
                    journal.SourceDisposition);
            });

            var template = journals[0];
            var bytes = await File.ReadAllBytesAsync(missingSource);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            pending.CompatibilityFilePublicationJournals.Add(
                new CompatibilityFilePublicationJournal
                {
                    OperationId = Guid.NewGuid(),
                    BatchId = scenario.BatchId,
                    ProtocolVersion = CompatibilityFilePublicationProtocol.Current,
                    RequestedAction = FileAction.Move,
                    EffectiveAction = FileAction.Copy,
                    SourceDisposition = CompatibilitySourceDisposition.Retained,
                    CleanupOwner = CompatibilityCleanupOwner.DownloadClient,
                    DestinationRootFolderId = template.DestinationRootFolderId,
                    DestinationPolicyRevision = template.DestinationPolicyRevision,
                    DestinationStorageContractRevision =
                        template.DestinationStorageContractRevision,
                    SourcePath = missingSource,
                    DestinationPath = missingDestination,
                    SourceLength = bytes.Length,
                    SourceSha256 = sha256,
                    TargetLength = bytes.Length,
                    TargetSha256 = sha256,
                    IsCompanionFile = true,
                    ExpectedBatchMemberCount = manifest.ExpectedMemberCount,
                    ExpectedBatchSourceManifestSha256 = manifest.SourceManifestSha256,
                    State = CompatibilityFilePublicationState.RegistrationCommitted
                });
            await pending.SaveChangesAsync();
        }

        await service.ReconcileAsync();

        await using var verification = await factory.CreateDbContextAsync();
        var recovered = await verification.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .Where(journal => journal.BatchId == scenario.BatchId)
            .ToListAsync();
        Assert.Equal(3, recovered.Count);
        Assert.All(recovered, journal =>
        {
            Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
            Assert.Equal(
                CompatibilitySourceDisposition.Retained,
                journal.SourceDisposition);
        });
    }

    [Fact]
    public async Task ReconcileAsync_LegacyBatchWithoutManifest_RecoversRetainOnly()
    {
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        var scenario = await CreateCommittedCompanionBatchAsync(
            factory,
            includeManifest: false);
        var service = CreateRecoveryService(factory);

        await service.ReconcileAsync();

        await using var verification = await factory.CreateDbContextAsync();
        var journals = await verification.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .Where(journal => journal.BatchId == scenario.BatchId)
            .ToListAsync();
        Assert.Equal(2, journals.Count);
        Assert.All(journals, journal =>
        {
            Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
            Assert.Equal(CompatibilitySourceDisposition.Retained, journal.SourceDisposition);
            Assert.Equal(
                "Interrupted compatibility batch recovered retain-only.",
                journal.Error);
        });
    }

    private CompatibilityFilePublicationRecoveryService CreateRecoveryService(
        IDbContextFactory<ListenArrDbContext> factory)
    {
        return new CompatibilityFilePublicationRecoveryService(
            factory,
            TimeProvider.System,
            NullLogger<CompatibilityFilePublicationRecoveryService>.Instance);
    }

    private async Task<CommittedBatchScenario> CreateCommittedCompanionBatchAsync(
        IDbContextFactory<ListenArrDbContext> factory,
        bool includeManifest)
    {
        var sourceDirectory = FileService.GetTempDirectory(
            "compatibility-recovery-manifest-source");
        var destinationRoot = FileService.GetTempDirectory(
            "compatibility-recovery-manifest-destination");
        var sources = new[]
        {
            Path.Join(sourceDirectory, "source-1.jpg"),
            Path.Join(sourceDirectory, "source-2.cue")
        };
        var destinations = new[]
        {
            Path.Join(destinationRoot, "book", "cover.jpg"),
            Path.Join(destinationRoot, "book", "book.cue")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(destinations[0])!);
        await File.WriteAllTextAsync(sources[0], "cover");
        await File.WriteAllTextAsync(sources[1], "chapters");
        await File.WriteAllTextAsync(destinations[0], "cover");
        await File.WriteAllTextAsync(destinations[1], "chapters");
        var batchId = Guid.NewGuid();
        var manifest = CompatibilityBatchManifest.Create(sources);

        await using var db = await factory.CreateDbContextAsync();
        var root = new RootFolder
        {
            Name = "Weak destination",
            Path = destinationRoot,
            PathIdentityState = PathIdentityState.Valid,
            ResolvedCaseSensitivity = FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
            WeakStorageSourceCleanupPolicy =
                WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy,
            WeakStoragePolicyRevision = 7,
            StorageContractRevision = 11
        };
        db.RootFolders.Add(root);
        await db.SaveChangesAsync();

        for (var index = 0; index < sources.Length; index++)
        {
            var bytes = await File.ReadAllBytesAsync(sources[index]);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            db.CompatibilityFilePublicationJournals.Add(
                new CompatibilityFilePublicationJournal
                {
                    OperationId = Guid.NewGuid(),
                    BatchId = batchId,
                    ProtocolVersion = CompatibilityFilePublicationProtocol.Current,
                    RequestedAction = FileAction.Move,
                    EffectiveAction = FileAction.Copy,
                    SourceDisposition = CompatibilitySourceDisposition.Retained,
                    CleanupOwner = CompatibilityCleanupOwner.DownloadClient,
                    DestinationRootFolderId = root.Id,
                    DestinationPolicyRevision = root.WeakStoragePolicyRevision,
                    DestinationStorageContractRevision = root.StorageContractRevision,
                    SourcePath = sources[index],
                    DestinationPath = destinations[index],
                    SourceLength = bytes.Length,
                    SourceSha256 = sha256,
                    TargetLength = bytes.Length,
                    TargetSha256 = sha256,
                    IsCompanionFile = true,
                    ExpectedBatchMemberCount = includeManifest
                        ? manifest.ExpectedMemberCount
                        : null,
                    ExpectedBatchSourceManifestSha256 = includeManifest
                        ? manifest.SourceManifestSha256
                        : null,
                    State = CompatibilityFilePublicationState.RegistrationCommitted
                });
        }
        await db.SaveChangesAsync();

        return new CommittedBatchScenario(batchId, sources);
    }

    private sealed record CommittedBatchScenario(
        Guid BatchId,
        IReadOnlyList<string> Sources);
}
