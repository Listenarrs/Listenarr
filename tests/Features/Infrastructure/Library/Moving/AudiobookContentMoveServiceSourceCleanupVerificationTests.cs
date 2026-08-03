using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_SourceGenerationReplacedAfterPublication_PreservesReplacement()
    {
        var source = FileService.GetTempDirectory("content-move-source-cleanup-generation-race");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-source-cleanup-generation-target-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        string physicalIdentity;
        using (var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            source,
            createMissing: false))
        using (var file = parent.OpenExistingFileForStableRead("book.m4b"))
        {
            physicalIdentity = file.GetObjectIdentity();
        }
        request = request with
        {
            SourcePhysicalObjectIdentities = new Dictionary<string, string>(
                request.SourceSemantics.Comparer)
            {
                ["book.m4b"] = physicalIdentity
            }
        };
        var displaced = sourceFile + ".original";
        var injector = new ReplaceSourceAfterPublication(
            sourceFile,
            displaced,
            "verified audio");
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            injector);

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.True(injector.ReplacementRan);
        Assert.True(File.Exists(sourceFile));
        Assert.True(File.Exists(displaced));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(displaced));
        Assert.Equal(
            "verified audio",
            await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_SourceCleanupCompleteWithRecreatedOwnedFile_RequiresAttention()
    {
        var state = await CreateSourceCleanupCompletedStateAsync(deleteEmptySource: true);
        Directory.CreateDirectory(state.Source);
        await FileService.GetFileAsync(state.Source, "book.m4b", "do not delete");
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(state.Request));

        Assert.Contains("owned file path", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "do not delete",
            await File.ReadAllTextAsync(Path.Join(state.Source, "book.m4b")));
        Assert.True(File.Exists(state.MarkerPath));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_SourceCleanupCompleteWithForeignFile_RemainsRecoverable()
    {
        var state = await CreateSourceCleanupCompletedStateAsync(deleteEmptySource: true);
        Directory.CreateDirectory(state.Source);
        var foreignFile = await FileService.GetFileAsync(
            state.Source,
            "operator-note.txt",
            "preserve me");
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var result = await service.GetRecoverableMoveAsync(state.Request);

        Assert.NotNull(result);
        Assert.True(result.SourceCleanupCompleted);
        Assert.Equal("preserve me", await File.ReadAllTextAsync(foreignFile));
        Assert.True(File.Exists(state.MarkerPath));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_SourceCleanupCompleteWithRecreatedEmptySource_RequiresAttention()
    {
        var state = await CreateSourceCleanupCompletedStateAsync(deleteEmptySource: true);
        Directory.CreateDirectory(state.Source);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.GetRecoverableMoveAsync(state.Request));

        Assert.Contains("recreated after cleanup", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(state.Source));
        Assert.True(File.Exists(state.MarkerPath));
    }

    [Fact]
    public async Task GetRecoverableMoveAsync_SourceCleanupCompleteRetainedEmptySource_IsValidWhenConfigured()
    {
        var state = await CreateSourceCleanupCompletedStateAsync(deleteEmptySource: false);
        Directory.CreateDirectory(state.Source);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        var result = await service.GetRecoverableMoveAsync(state.Request);

        Assert.NotNull(result);
        Assert.True(result.SourceCleanupCompleted);
        Assert.True(Directory.Exists(state.Source));
        Assert.True(File.Exists(state.MarkerPath));
    }

    private async Task<SourceCleanupCompletedState> CreateSourceCleanupCompletedStateAsync(
        bool deleteEmptySource)
    {
        var source = FileService.GetTempDirectory("content-move-source-cleanup-state-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-source-cleanup-state-dst");
        await FileService.GetFileAsync(target, "book.m4b", "verified audio");
        var jobId = Guid.NewGuid();
        var request = await CreateLeasedMoveRequestAsync(
            source,
            target,
            jobId,
            deleteEmptySource);
        await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
        Directory.Delete(source, recursive: true);
        await WriteRecoveryMarkerAsync(
            target,
            jobId,
            source,
            target,
            "source-cleanup-complete");
        return new SourceCleanupCompletedState(
            source,
            target,
            Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
            request);
    }

    private sealed class ReplaceSourceAfterPublication(
        string sourceFile,
        string displaced,
        string contents) : IMoveFaultInjector
    {
        public bool AllowAtomicRename => false;

        public bool ReplacementRan { get; private set; }

        public Task AfterPublishedAsync(
            Guid jobId,
            CancellationToken cancellationToken)
        {
            File.Move(sourceFile, displaced);
            File.WriteAllText(sourceFile, contents);
            ReplacementRan = true;
            return Task.CompletedTask;
        }
    }

    private sealed record SourceCleanupCompletedState(
        string Source,
        string Target,
        string MarkerPath,
        AudiobookContentMoveRequest Request);
}
