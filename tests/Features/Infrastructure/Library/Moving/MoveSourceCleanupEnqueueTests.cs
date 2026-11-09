using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "MoveSourceCleanupEnqueueTests")]
[Trait("Category", "Infrastructure")]
public sealed class MoveSourceCleanupEnqueueTests : BaseTests
{
    [Fact]
    public async Task EnqueueMoveAsync_ResolverDeleteAuthorization_Succeeds()
    {
        var (authorization, command) = await ResolveAndBuildCommandAsync(
            "enqueue-resolver-delete", targetInsideRoot: true);
        Assert.True(authorization.DeletesSourceAfterVerifiedCopy);

        var jobId = await _provider.GetRequiredService<IMoveQueueService>()
            .EnqueueMoveAsync(command);

        var job = await _provider.GetRequiredService<IMoveQueueService>()
            .GetJobAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(MoveSourceCleanupMode.DeleteAfterVerifiedCopy, job.SourceCleanupMode);
        Assert.Equal(authorization.SourceStorageContractRevision, job.SourceStorageContractRevision);
        Assert.Equal(authorization.TargetStorageContractRevision, job.TargetStorageContractRevision);
    }

    [Fact]
    public async Task EnqueueMoveAsync_DeleteAuthorizationWithPolicyRevisionsSupplied_Succeeds()
    {
        var (authorization, command) = await ResolveAndBuildCommandAsync(
            "enqueue-handfed-delete", targetInsideRoot: true);
        Assert.True(authorization.DeletesSourceAfterVerifiedCopy);

        var jobId = await _provider.GetRequiredService<IMoveQueueService>()
            .EnqueueMoveAsync(command with
            {
                SourcePolicyRevision = 0,
                TargetPolicyRevision = 0
            });

        Assert.NotEqual(Guid.Empty, jobId);
    }

    [Fact]
    public async Task EnqueueMoveAsync_DeleteAuthorizationMissingTargetStorageRevision_IsRejected()
    {
        var (_, command) = await ResolveAndBuildCommandAsync(
            "enqueue-missing-target-contract", targetInsideRoot: true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.GetRequiredService<IMoveQueueService>()
                .EnqueueMoveAsync(command with { TargetStorageContractRevision = null }));
    }

    [Fact]
    public async Task EnqueueMoveAsync_DeleteAuthorizationMissingSourceStorageRevision_IsRejected()
    {
        var (authorization, command) = await ResolveAndBuildCommandAsync(
            "enqueue-missing-source-contract", targetInsideRoot: true);
        Assert.NotNull(authorization.SourceRootFolderId);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.GetRequiredService<IMoveQueueService>()
                .EnqueueMoveAsync(command with { SourceStorageContractRevision = null }));
    }

    [Fact]
    public async Task EnqueueMoveAsync_ResolverRetainAuthorization_Succeeds()
    {
        var (authorization, command) = await ResolveAndBuildCommandAsync(
            "enqueue-resolver-retain", targetInsideRoot: false);
        Assert.False(authorization.DeletesSourceAfterVerifiedCopy);

        var jobId = await _provider.GetRequiredService<IMoveQueueService>()
            .EnqueueMoveAsync(command);

        Assert.NotEqual(Guid.Empty, jobId);
    }

    private async Task<(MoveSourceCleanupAuthorization Authorization, MoveEnqueueCommand Command)>
        ResolveAndBuildCommandAsync(string name, bool targetInsideRoot)
    {
        var sourcePath = FileService.GetTempDirectory($"{name}-source");
        var targetRootPath = FileService.GetTempDirectory($"{name}-target");
        await AddAuthorizedRootAsync(
            sourcePath, "Source", FileSystemCaseSensitivityMode.Sensitive);
        if (targetInsideRoot)
        {
            await AddAuthorizedRootAsync(
                targetRootPath, "Target", FileSystemCaseSensitivityMode.Sensitive);
        }
        var targetPath = Path.Join(targetRootPath, "Author", "Book");
        var authorization = await _provider
            .GetRequiredService<IMoveSourceCleanupPolicyResolver>()
            .ResolveAsync(sourcePath, targetPath);

        var semantics = _provider.GetRequiredService<IFileSystemSemanticsResolver>();
        var sourceResolution = await semantics.ResolveAsync(sourcePath);
        var targetResolution = await semantics.ResolveAsync(targetPath);
        var audiobook = await CreateAudiobook();
        var command = new MoveEnqueueCommand(
            audiobook.Id,
            sourcePath,
            PathIdentitySnapshot.FromResolution(
                sourceResolution.Semantics,
                FileSystemCaseSensitivityMode.Sensitive,
                sourceResolution.BoundaryPath,
                sourcePath),
            [
                new MoveSourceManifestEntry(
                    "book.m4b",
                    MoveJobEntryType.File,
                    1,
                    DateTime.UnixEpoch,
                    new string('A', 64))
            ],
            targetPath,
            PathIdentitySnapshot.FromResolution(
                targetResolution.Semantics,
                FileSystemCaseSensitivityMode.Sensitive,
                targetResolution.BoundaryPath,
                targetPath),
            DeleteEmptySource: false,
            SourceCleanupMode: authorization.Mode,
            SourceRootFolderId: authorization.SourceRootFolderId,
            SourcePolicyRevision: authorization.SourcePolicyRevision,
            TargetRootFolderId: authorization.TargetRootFolderId,
            TargetPolicyRevision: authorization.TargetPolicyRevision,
            SourceStorageContractRevision: authorization.SourceStorageContractRevision,
            TargetStorageContractRevision: authorization.TargetStorageContractRevision,
            ForceCopyAndRetainSource: authorization.ForceCopyAndRetainSource);
        return (authorization, command);
    }
}
