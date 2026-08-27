using System.Security.Cryptography;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "CompatibilitySourceCleanupCoordinatorTests")]
[Trait("Category", "Infrastructure")]
public sealed class CompatibilitySourceCleanupCoordinatorTests : BaseTests
{
    [Fact]
    public async Task CompleteBatchAsync_ListenarrOwner_VerifiesAndRemovesSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        var service = CreateService(scenario.Factory);

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(
            CompatibilityBatchCleanupDisposition.RetiredByListenarr,
            result.Disposition);
        Assert.Equal(1, result.RemovedCount);
        Assert.False(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Destination));
        Assert.False(Directory.Exists(Path.Join(
            Path.GetDirectoryName(scenario.Source)!,
            ".listenarr-quarantine-" + scenario.BatchId.ToString("N"))));
        var journal = await LoadJournalAsync(scenario);
        Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
        Assert.Equal(
            CompatibilitySourceDisposition.RetiredByListenarr,
            journal.SourceDisposition);
    }

    [Fact]
    public async Task CompleteBatchAsync_DownloadClientOwner_DefersWithoutDeletingSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.DownloadClient);
        var service = CreateService(scenario.Factory);

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(
            CompatibilityBatchCleanupDisposition.DeferredToDownloadClient,
            result.Disposition);
        Assert.True(File.Exists(scenario.Source));
        var journal = await LoadJournalAsync(scenario);
        Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
        Assert.Equal(
            CompatibilitySourceDisposition.DeferredToDownloadClient,
            journal.SourceDisposition);
    }

    [Fact]
    public async Task CompleteBatchAsync_ChangedPolicyRevision_RetainsSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        await using (var db = await scenario.Factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync(root => root.Id == scenario.RootId);
            root.WeakStoragePolicyRevision++;
            await db.SaveChangesAsync();
        }
        var service = CreateService(scenario.Factory);

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(CompatibilityBatchCleanupDisposition.Retained, result.Disposition);
        Assert.True(File.Exists(scenario.Source));
        var journal = await LoadJournalAsync(scenario);
        Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
        Assert.Equal(CompatibilitySourceDisposition.Retained, journal.SourceDisposition);
    }

    [Fact]
    public async Task CompleteBatchAsync_ChangedStorageContractRevision_RetainsSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        await using (var db = await scenario.Factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync(root => root.Id == scenario.RootId);
            root.StorageContractRevision++;
            await db.SaveChangesAsync();
        }
        var service = CreateService(scenario.Factory);

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(CompatibilityBatchCleanupDisposition.Retained, result.Disposition);
        Assert.True(File.Exists(scenario.Source));
        var journal = await LoadJournalAsync(scenario);
        Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
        Assert.Equal(CompatibilitySourceDisposition.Retained, journal.SourceDisposition);
    }

    [Fact]
    public async Task CompleteBatchAsync_TargetStorageAuthorityLost_RetainsSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        var service = CreateService(
            scenario.Factory,
            new RootFolderStorageObservation(
                RootFolderStorageState.Changed,
                RootFolderStorageReason.IdentityMismatch,
                "Target changed",
                CanConfirmCurrentFolder: true,
                CanChangePath: true,
                CanMutateFilesystem: false,
                ConfirmationToken: "refresh"));

        var result = await service.CompleteBatchAsync(
            scenario.BatchId,
            batchSucceeded: true);

        Assert.Equal(CompatibilityBatchCleanupDisposition.Retained, result.Disposition);
        Assert.True(File.Exists(scenario.Source));
        var journal = await LoadJournalAsync(scenario);
        Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
        Assert.Equal(CompatibilitySourceDisposition.Retained, journal.SourceDisposition);
    }

    [Fact]
    public async Task CompleteBatchAsync_TargetMismatch_RetainsEntireBatch()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        await File.WriteAllTextAsync(scenario.Destination, "different");
        var service = CreateService(scenario.Factory);

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(CompatibilityBatchCleanupDisposition.Retained, result.Disposition);
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal(0, result.RemovedCount);
    }

    [FileLinkFact]
    public async Task CompleteBatchAsync_TargetReplacedBySymlinkWithMatchingContent_RetainsSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        var externalDirectory = FileService.GetTempDirectory("cleanup-external-target");
        var external = Path.Join(externalDirectory, "external.m4b");
        await File.WriteAllTextAsync(external, "audio");
        File.Delete(scenario.Destination);
        File.CreateSymbolicLink(scenario.Destination, external);
        var service = CreateService(scenario.Factory);

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(CompatibilityBatchCleanupDisposition.Retained, result.Disposition);
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(external));
        Assert.Equal(0, result.RemovedCount);
    }

    [Fact]
    public async Task CompleteBatchAsync_FailureImmediatelyAfterQuarantineMove_RestoresSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        var service = CreateService(scenario.Factory);
        service.AfterSourceMovedToQuarantineForTest = () =>
            throw new InvalidOperationException("Injected failure after quarantine rename.");

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(CompatibilityBatchCleanupDisposition.Retained, result.Disposition);
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        var journal = await LoadJournalAsync(scenario);
        Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
        Assert.Equal(CompatibilitySourceDisposition.Retained, journal.SourceDisposition);
    }

    [Fact]
    public async Task CompleteBatchAsync_DestinationChangesAfterQuarantine_RestoresSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        var service = CreateService(scenario.Factory);
        service.AfterBatchQuarantinedForTest = () =>
            File.WriteAllText(scenario.Destination, "changed-after-quarantine");

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(CompatibilityBatchCleanupDisposition.Retained, result.Disposition);
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("audio", await File.ReadAllTextAsync(scenario.Source));
        var journal = await LoadJournalAsync(scenario);
        Assert.Equal(CompatibilityFilePublicationState.Completed, journal.State);
        Assert.Equal(CompatibilitySourceDisposition.Retained, journal.SourceDisposition);
    }

    [Fact]
    public async Task CompleteBatchAsync_LaterDestinationChangesDuringDeletion_RestoresRemainingSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        var secondSource = Path.Join(Path.GetDirectoryName(scenario.Source)!, "source-2.m4b");
        var secondDestination = Path.Join(Path.GetDirectoryName(scenario.Destination)!, "target-2.m4b");
        await File.WriteAllTextAsync(secondSource, "audio-2");
        await File.WriteAllTextAsync(secondDestination, "audio-2");
        var bytes = await File.ReadAllBytesAsync(secondSource);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var secondOperationId = Guid.NewGuid();
        await using (var db = await scenario.Factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync(root => root.Id == scenario.RootId);
            db.CompatibilityFilePublicationJournals.Add(
                new CompatibilityFilePublicationJournal
                {
                    OperationId = secondOperationId,
                    BatchId = scenario.BatchId,
                    ProtocolVersion = CompatibilityFilePublicationProtocol.Current,
                    RequestedAction = FileAction.Move,
                    EffectiveAction = FileAction.Copy,
                    SourceDisposition = CompatibilitySourceDisposition.Retained,
                    CleanupOwner = CompatibilityCleanupOwner.Listenarr,
                    DestinationRootFolderId = scenario.RootId,
                    DestinationPolicyRevision = root.WeakStoragePolicyRevision,
                    DestinationStorageContractRevision = root.StorageContractRevision,
                    SourcePath = secondSource,
                    DestinationPath = secondDestination,
                    SourceLength = bytes.Length,
                    SourceSha256 = sha256,
                    TargetLength = bytes.Length,
                    TargetSha256 = sha256,
                    State = CompatibilityFilePublicationState.RegistrationCommitted,
                    CreatedAt = DateTime.UtcNow.AddMinutes(1)
                });
            await db.SaveChangesAsync();
        }
        var service = CreateService(scenario.Factory);
        service.BeforeSourceDeleteForTest = journal =>
        {
            if (journal.OperationId == secondOperationId)
            {
                File.WriteAllText(secondDestination, "changed-during-delete");
            }
        };

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(CompatibilityBatchCleanupDisposition.PartialNeedsAttention, result.Disposition);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(1, result.RetainedCount);
        Assert.False(File.Exists(scenario.Source));
        Assert.True(File.Exists(secondSource));
        Assert.Equal("audio-2", await File.ReadAllTextAsync(secondSource));
        await using var verification = await scenario.Factory.CreateDbContextAsync();
        var firstJournal = await verification.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .SingleAsync(journal => journal.OperationId == scenario.OperationId);
        var secondJournal = await verification.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .SingleAsync(journal => journal.OperationId == secondOperationId);
        Assert.Equal(CompatibilitySourceDisposition.RetiredByListenarr, firstJournal.SourceDisposition);
        Assert.Equal(CompatibilityFilePublicationState.Completed, firstJournal.State);
        Assert.Equal(CompatibilitySourceDisposition.Retained, secondJournal.SourceDisposition);
        Assert.Equal(CompatibilityFilePublicationState.Completed, secondJournal.State);
    }

    [Fact]
    public async Task CompleteBatchAsync_PreexistingUnownedQuarantine_RetainsSource()
    {
        var scenario = await CreateScenarioAsync(CompatibilityCleanupOwner.Listenarr);
        var quarantine = Path.Join(
            Path.GetDirectoryName(scenario.Source)!,
            ".listenarr-quarantine-" + scenario.BatchId.ToString("N"));
        Directory.CreateDirectory(quarantine);
        var sentinel = Path.Join(quarantine, "user-file.txt");
        await File.WriteAllTextAsync(sentinel, "not-listenarr-owned");
        var service = CreateService(scenario.Factory);

        var result = await service.CompleteBatchAsync(scenario.BatchId, batchSucceeded: true);

        Assert.Equal(CompatibilityBatchCleanupDisposition.Retained, result.Disposition);
        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("not-listenarr-owned", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Join(quarantine, ".listenarr-owner-v2")));
    }

    private CompatibilitySourceCleanupCoordinator CreateService(
        IDbContextFactory<ListenArrDbContext> factory,
        RootFolderStorageObservation? observation = null)
    {
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<RootFolder>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(observation ?? HealthyStorage());
        return new CompatibilitySourceCleanupCoordinator(
            factory,
            health.Object,
            TimeProvider.System,
            NullLogger<CompatibilitySourceCleanupCoordinator>.Instance);
    }

    private static RootFolderStorageObservation HealthyStorage() =>
        new(
            RootFolderStorageState.Healthy,
            RootFolderStorageReason.None,
            Message: null,
            CanConfirmCurrentFolder: false,
            CanChangePath: true,
            CanMutateFilesystem: true,
            ConfirmationToken: null);

    private async Task<Scenario> CreateScenarioAsync(CompatibilityCleanupOwner owner)
    {
        var sourceDirectory = FileService.GetTempDirectory("cleanup-source");
        var destinationRoot = FileService.GetTempDirectory("cleanup-destination");
        var source = Path.Join(sourceDirectory, "source.m4b");
        var destination = Path.Join(destinationRoot, "book", "target.m4b");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(source, "audio");
        await File.WriteAllTextAsync(destination, "audio");
        var bytes = await File.ReadAllBytesAsync(source);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var batchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        int rootId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Weak destination",
                Path = destinationRoot,
                WeakStorageSourceCleanupPolicy =
                    WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy,
                WeakStoragePolicyRevision = 3,
                StorageContractRevision = 5
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
            db.CompatibilityFilePublicationJournals.Add(
                new CompatibilityFilePublicationJournal
                {
                    OperationId = operationId,
                    BatchId = batchId,
                    ProtocolVersion = CompatibilityFilePublicationProtocol.Current,
                    RequestedAction = FileAction.Move,
                    EffectiveAction = FileAction.Copy,
                    SourceDisposition = CompatibilitySourceDisposition.Retained,
                    CleanupOwner = owner,
                    DestinationRootFolderId = rootId,
                    DestinationPolicyRevision = root.WeakStoragePolicyRevision,
                    DestinationStorageContractRevision = root.StorageContractRevision,
                    SourcePath = source,
                    DestinationPath = destination,
                    SourceLength = bytes.Length,
                    SourceSha256 = sha256,
                    TargetLength = bytes.Length,
                    TargetSha256 = sha256,
                    State = CompatibilityFilePublicationState.RegistrationCommitted
                });
            await db.SaveChangesAsync();
        }

        return new Scenario(
            factory,
            batchId,
            operationId,
            rootId,
            source,
            destination);
    }

    private static async Task<CompatibilityFilePublicationJournal> LoadJournalAsync(
        Scenario scenario)
    {
        await using var db = await scenario.Factory.CreateDbContextAsync();
        return await db.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .SingleAsync(journal => journal.OperationId == scenario.OperationId);
    }

    private sealed record Scenario(
        IDbContextFactory<ListenArrDbContext> Factory,
        Guid BatchId,
        Guid OperationId,
        int RootId,
        string Source,
        string Destination);
}
