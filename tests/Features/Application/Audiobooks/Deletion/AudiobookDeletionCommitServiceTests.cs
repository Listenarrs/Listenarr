using Listenarr.Application.Audiobooks.Deletion;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Deletion;

[Trait("Area", "Library")]
[Trait("Name", "AudiobookDeletionCommitServiceTests")]
[Trait("Category", "Application")]
public sealed class AudiobookDeletionCommitServiceTests : BaseTests
{
    [Fact]
    public async Task DeleteAsync_RequestCanceledWhilePreflightCompletes_DoesNotCommit()
    {
        // Given
        const int audiobookId = 4101;
        var audiobook = new Audiobook
        {
            Id = audiobookId,
            Title = "Cancelable delete"
        };
        var preflightStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreflight = new TaskCompletionSource<Audiobook?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetForUpdateSnapshotAsync(
                audiobookId,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                preflightStarted.SetResult();
                return await releasePreflight.Task;
            });
        using var cancellation = new CancellationTokenSource();
        var service = new AudiobookDeletionCommitService(repository.Object);

        // When
        var deletion = service.DeleteAsync(audiobookId, cancellation.Token);
        await preflightStarted.Task;
        cancellation.Cancel();
        releasePreflight.SetResult(audiobook);

        // Then
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion);
        repository.Verify(service => service.DeleteByIdAsync(audiobookId), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_RequestCanceledAfterCommitBoundary_CompletesCommit()
    {
        // Given
        const int audiobookId = 4102;
        var audiobook = new Audiobook
        {
            Id = audiobookId,
            Title = "Committed delete"
        };
        using var cancellation = new CancellationTokenSource();
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetForUpdateSnapshotAsync(
                audiobookId,
                cancellation.Token))
            .ReturnsAsync(audiobook);
        repository.Setup(service => service.DeleteByIdAsync(audiobookId))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            });
        var service = new AudiobookDeletionCommitService(repository.Object);

        // When
        var result = await service.DeleteAsync(audiobookId, cancellation.Token);

        // Then
        Assert.Equal(AudiobookDeletionCommitOutcome.Deleted, result.Outcome);
        Assert.Same(audiobook, result.Audiobook);
        Assert.True(cancellation.IsCancellationRequested);
        repository.Verify(service => service.DeleteByIdAsync(audiobookId), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_IncludeFiles_UsesFullSnapshotForPostCommitFilesystemCleanup()
    {
        // Given
        const int audiobookId = 4104;
        var audiobook = new Audiobook
        {
            Id = audiobookId,
            Title = "Filesystem delete",
            Files = [AudiobookFile.CreateUnresolved("/library/book.m4b")]
        };
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetForUpdateSnapshotAsync(
                audiobookId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Audiobook
            {
                Id = audiobookId,
                Title = audiobook.Title
            });
        repository.Setup(service => service.GetByIdSnapshotAsync(
                audiobookId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(audiobook);
        repository.Setup(service => service.DeleteByIdAsync(audiobookId))
            .ReturnsAsync(true);
        var service = new AudiobookDeletionCommitService(repository.Object);

        // When
        var result = await service.DeleteAsync(
            audiobookId,
            includeFiles: true,
            CancellationToken.None);

        // Then
        Assert.Equal(AudiobookDeletionCommitOutcome.Deleted, result.Outcome);
        Assert.Same(audiobook, result.Audiobook);
        repository.Verify(service => service.GetForUpdateSnapshotAsync(
            audiobookId,
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(service => service.GetByIdSnapshotAsync(
            audiobookId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_IncludeFilesWithForeignHostBoundary_DoesNotLoadFileGraph()
    {
        // Given
        const int audiobookId = 4105;
        var foreignBasePath = OperatingSystem.IsWindows()
            ? "/server/mnt/drive/Audiobooks/Imported"
            : @"C:\server\Audiobooks\Imported";
        var audiobook = new Audiobook
        {
            Id = audiobookId,
            Title = "Copied database",
            BasePath = foreignBasePath
        };
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetForUpdateSnapshotAsync(
                audiobookId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(audiobook);
        repository.Setup(service => service.DeleteByIdAsync(audiobookId))
            .ReturnsAsync(true);
        var service = new AudiobookDeletionCommitService(repository.Object);

        // When
        var result = await service.DeleteAsync(
            audiobookId,
            includeFiles: true,
            CancellationToken.None);

        // Then
        Assert.Equal(AudiobookDeletionCommitOutcome.Deleted, result.Outcome);
        Assert.Same(audiobook, result.Audiobook);
        repository.Verify(service => service.GetByIdSnapshotAsync(
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_CanceledBeforePreflight_DoesNotCommit()
    {
        // Given
        const int audiobookId = 4103;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var service = new AudiobookDeletionCommitService(repository.Object);

        // When / Then
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DeleteAsync(audiobookId, cancellation.Token));
        repository.Verify(service => service.GetForUpdateSnapshotAsync(
            audiobookId,
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(service => service.DeleteByIdAsync(audiobookId), Times.Never);
    }
}
