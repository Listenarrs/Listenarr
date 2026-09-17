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

    [Fact]
    public async Task PrepareAsync_ContentOnlySource_PublishesTargetWithoutRetiringSource_AndRollsBackExactly()
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
                CanRetireWithDurableIdentity: false,
                CanRetireAfterVerifiedCopy: true));
        return new VerifiedFileRenameTransactionCoordinator(
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            _rootFolderRepository,
            health.Object,
            TimeProvider.System,
            NullLogger<VerifiedFileRenameTransactionCoordinator>.Instance);
    }

    private async Task<Scenario> CreateScenarioAsync()
    {
        var rootPath = FileService.GetTempDirectory("verified-organize-coordinator");
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
