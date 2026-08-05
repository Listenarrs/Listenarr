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
        repository.Setup(service => service.GetByIdSnapshotAsync(
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
        repository.Setup(service => service.GetByIdSnapshotAsync(
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
        repository.Verify(service => service.GetByIdSnapshotAsync(
            audiobookId,
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(service => service.DeleteByIdAsync(audiobookId), Times.Never);
    }
}
