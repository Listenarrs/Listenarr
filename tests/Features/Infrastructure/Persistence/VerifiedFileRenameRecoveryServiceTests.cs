using System.Security.Cryptography;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "VerifiedFileRenameRecoveryServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class VerifiedFileRenameRecoveryServiceTests : BaseTests
{
    [Fact]
    public async Task FileRenameRecoveryProbe_ActiveVerifiedJournal_BlocksUntilTerminalState()
    {
        var scenario = await CreateScenarioAsync(
            VerifiedFileRenameState.TargetVerified,
            ownerAtDestination: false,
            createDestination: true);
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        var probe = new FileRenameRecoveryProbe(factory);

        Assert.True(await probe.HasBlockingAsync(scenario.AudiobookId));

        await using (var db = await factory.CreateDbContextAsync())
        {
            var journal = await db.VerifiedFileRenameJournals
                .SingleAsync(candidate => candidate.OperationId == scenario.OperationId);
            journal.State = VerifiedFileRenameState.CompletedSourceRetained;
            await db.SaveChangesAsync();
        }

        Assert.False(await probe.HasBlockingAsync(scenario.AudiobookId));
    }

    [Fact]
    public async Task ReconcileAsync_OwnerCommittedWithOldSourcePresent_CompletesSourceRetainedWithoutDeleting()
    {
        var scenario = await CreateScenarioAsync(
            VerifiedFileRenameState.OwnerMetadataReconciled,
            ownerAtDestination: true,
            createDestination: true);
        var service = CreateService();

        await service.ReconcileAsync();

        Assert.True(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        var journal = await GetJournalAsync(scenario.OperationId);
        Assert.Equal(VerifiedFileRenameState.CompletedSourceRetained, journal.State);
        Assert.Contains("retained", journal.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAsync_OwnerCommittedAfterSourceWasDeleted_CompletesWithoutFurtherMutation()
    {
        var scenario = await CreateScenarioAsync(
            VerifiedFileRenameState.OwnerMetadataReconciled,
            ownerAtDestination: true,
            createDestination: true);
        File.Delete(scenario.Source);
        var service = CreateService();

        await service.ReconcileAsync();

        Assert.False(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        Assert.Equal(
            VerifiedFileRenameState.Completed,
            (await GetJournalAsync(scenario.OperationId)).State);
    }

    [Fact]
    public async Task ReconcileAsync_TargetVerifiedBeforeOwnerCommit_PreservesBothPathsAndRequiresAttention()
    {
        var scenario = await CreateScenarioAsync(
            VerifiedFileRenameState.TargetVerified,
            ownerAtDestination: false,
            createDestination: true);
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileAsync());

        Assert.True(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.Destination));
        var journal = await GetJournalAsync(scenario.OperationId);
        Assert.Equal(VerifiedFileRenameState.NeedsAttention, journal.State);
        Assert.Contains("will not delete", journal.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAsync_PlannedBeforePublication_RollsBackByDatabaseStateOnly()
    {
        var scenario = await CreateScenarioAsync(
            VerifiedFileRenameState.Planned,
            ownerAtDestination: false,
            createDestination: false);
        var service = CreateService();

        await service.ReconcileAsync();

        Assert.True(File.Exists(scenario.Source));
        Assert.False(File.Exists(scenario.Destination));
        Assert.False(File.Exists(scenario.StagingPath));
        Assert.Equal(
            VerifiedFileRenameState.RolledBack,
            (await GetJournalAsync(scenario.OperationId)).State);
    }

    [Fact]
    public async Task ReconcileAsync_SourceQuarantined_PreservesRetirementArtifactAndRequiresAttention()
    {
        var scenario = await CreateScenarioAsync(
            VerifiedFileRenameState.SourceQuarantined,
            ownerAtDestination: true,
            createDestination: true);
        File.Move(scenario.Source, scenario.RetirementPath);
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileAsync());

        Assert.False(File.Exists(scenario.Source));
        Assert.True(File.Exists(scenario.RetirementPath));
        Assert.Equal(
            "verified-recovery-audio",
            await File.ReadAllTextAsync(scenario.RetirementPath));
        var journal = await GetJournalAsync(scenario.OperationId);
        Assert.Equal(VerifiedFileRenameState.NeedsAttention, journal.State);
        Assert.Contains("retirement", journal.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAsync_SourceDeletedStateWithReappearedPath_DoesNotDeleteReplacement()
    {
        var scenario = await CreateScenarioAsync(
            VerifiedFileRenameState.SourceDeleted,
            ownerAtDestination: true,
            createDestination: true);
        await File.WriteAllTextAsync(scenario.Source, "reappeared-source");
        var service = CreateService();

        await service.ReconcileAsync();

        Assert.True(File.Exists(scenario.Source));
        Assert.Equal("reappeared-source", await File.ReadAllTextAsync(scenario.Source));
        Assert.Equal(
            VerifiedFileRenameState.CompletedSourceRetained,
            (await GetJournalAsync(scenario.OperationId)).State);
    }

    [Fact]
    public async Task ReconcileAsync_LegacyOwner_UsesResolvedOwnershipIdentityInsteadOfHostPathComparison()
    {
        var rootPath = FileService.GetTempDirectory("verified-organize-legacy-recovery");
        var source = Path.Join(rootPath, "old.m4b");
        var destination = Path.Join(rootPath, "new.m4b");
        var logicalDestinationAlias = Path.Join(rootPath, "NEW-ALIAS.m4b");
        await File.WriteAllTextAsync(source, "verified-legacy-recovery-audio");
        File.Copy(source, destination);

        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Verified Legacy Recovery",
            BasePath = rootPath,
            FilePath = logicalDestinationAlias
        });
        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var manifest = VerifiedFileRenameBatchManifest.Create(
        [
            new VerifiedFileRenameBatchMember(0, source, destination)
        ]);
        var bytes = await File.ReadAllBytesAsync(source);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.VerifiedFileRenameJournals.Add(new VerifiedFileRenameJournal
            {
                OperationId = operationId,
                BatchId = batchId,
                AudiobookId = audiobook.Id,
                AudiobookFileId = 0,
                ExpectedBatchMemberCount = manifest.ExpectedMemberCount,
                ExpectedBatchManifestSha256 = manifest.ManifestSha256,
                SourcePath = source,
                DestinationPath = destination,
                StagingPath = Path.Join(rootPath, ".listenarr-organize-legacy.partial"),
                RetirementPath = Path.Join(
                    rootPath,
                    ".listenarr-organize-" + operationId.ToString("N") + ".source"),
                SourceLength = bytes.LongLength,
                SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                SourceRootFolderId = 1,
                SourceStorageContractRevision = 1,
                DestinationRootFolderId = 1,
                DestinationStorageContractRevision = 1,
                State = VerifiedFileRenameState.OwnerMetadataReconciled
            });
            await db.SaveChangesAsync();
        }

        var ownershipIdentity = new AudiobookFilePathIdentity(
            Path.GetFullPath(destination),
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
            FileSystemCaseSensitivityMode.Auto,
            rootPath,
            "legacy-lookup",
            "legacy-shared-owner",
            AudiobookFilePathIdentity.CurrentVersion,
            PathIdentityState.Valid);
        var identityResolver = new Mock<IAudiobookFilePathIdentityResolver>(MockBehavior.Strict);
        identityResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ownershipIdentity));
        var service = new VerifiedFileRenameRecoveryService(
            factory,
            identityResolver.Object,
            TimeProvider.System,
            NullLogger<VerifiedFileRenameRecoveryService>.Instance);

        await service.ReconcileAsync();

        var journal = await GetJournalAsync(operationId);
        Assert.Equal(VerifiedFileRenameState.CompletedSourceRetained, journal.State);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
        identityResolver.Verify(resolver => resolver.ResolveAsync(
            It.IsAny<Audiobook>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private VerifiedFileRenameRecoveryService CreateService() =>
        new(
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>(),
            TimeProvider.System,
            NullLogger<VerifiedFileRenameRecoveryService>.Instance);

    private async Task<Scenario> CreateScenarioAsync(
        VerifiedFileRenameState state,
        bool ownerAtDestination,
        bool createDestination)
    {
        var rootPath = FileService.GetTempDirectory("verified-organize-recovery");
        var root = await AddAuthorizedRootAsync(rootPath);
        root.StorageContractRevision = 8;
        await _rootFolderRepository.UpdateAsync(root);

        var source = Path.Join(rootPath, "old.m4b");
        var destination = Path.Join(rootPath, "new.m4b");
        await File.WriteAllTextAsync(source, "verified-recovery-audio");
        if (createDestination)
        {
            File.Copy(source, destination);
        }

        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Verified Recovery",
            BasePath = rootPath
        });
        var identityResolver = _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>();
        var ownerPath = ownerAtDestination ? destination : source;
        var ownerIdentity = await identityResolver.ResolveAsync(
            audiobook,
            ownerPath);
        Assert.Equal(PathIdentityState.Valid, ownerIdentity.State);
        var file = new AudiobookFile
        {
            AudiobookId = audiobook.Id,
            Path = ownerPath,
            Size = new FileInfo(source).Length,
            Format = "m4b"
        };
        file.ApplyPathIdentity(ownerPath, ownerIdentity);
        file.ClearPhysicalObjectIdentity();
        file = await _audiobookFileRepository.AddAsync(file);

        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var stagingPath = Path.Join(
            rootPath,
            ".listenarr-organize-" + operationId.ToString("N") + ".partial");
        var retirementPath = Path.Join(
            rootPath,
            ".listenarr-organize-" + operationId.ToString("N") + ".source");
        var manifest = VerifiedFileRenameBatchManifest.Create(
        [
            new VerifiedFileRenameBatchMember(file.Id, source, destination)
        ]);
        var originalBytes = System.Text.Encoding.UTF8.GetBytes(
            "verified-recovery-audio");
        var factory = _provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.VerifiedFileRenameJournals.Add(new VerifiedFileRenameJournal
        {
            OperationId = operationId,
            BatchId = batchId,
            AudiobookId = audiobook.Id,
            AudiobookFileId = file.Id,
            ExpectedBatchMemberCount = manifest.ExpectedMemberCount,
            ExpectedBatchManifestSha256 = manifest.ManifestSha256,
            SourcePath = source,
            DestinationPath = destination,
            StagingPath = stagingPath,
            RetirementPath = retirementPath,
            SourceLength = originalBytes.LongLength,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(originalBytes)),
            SourceRootFolderId = root.Id,
            SourceStorageContractRevision = root.StorageContractRevision,
            DestinationRootFolderId = root.Id,
            DestinationStorageContractRevision = root.StorageContractRevision,
            State = state
        });
        await db.SaveChangesAsync();

        return new Scenario(
            operationId,
            audiobook.Id,
            source,
            destination,
            stagingPath,
            retirementPath);
    }

    private async Task<VerifiedFileRenameJournal> GetJournalAsync(Guid operationId)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.VerifiedFileRenameJournals
            .AsNoTracking()
            .SingleAsync(journal => journal.OperationId == operationId);
    }

    private sealed record Scenario(
        Guid OperationId,
        int AudiobookId,
        string Source,
        string Destination,
        string StagingPath,
        string RetirementPath);
}
