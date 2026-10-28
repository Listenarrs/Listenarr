using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Area", "LibraryApi")]
[Trait("Name", "LibraryBulkDeleteCancellationTests")]
[Trait("Category", "LibraryController")]
public sealed class LibraryBulkDeleteCancellationTests : BaseTests
{
    [Fact]
    public async Task BulkDelete_RequestCanceledWhilePreflightCompletes_DoesNotCommit()
    {
        // Given
        const int audiobookId = 4201;
        var audiobook = new Audiobook
        {
            Id = audiobookId,
            Title = "Cancelable bulk delete"
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
        var imageCache = new Mock<IImageCacheService>(MockBehavior.Strict);
        var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        Init(services => services
            .WithSingleton<IAudiobookRepository>(repository.Object)
            .WithSingleton<IImageCacheService>(imageCache.Object)
            .WithSingleton<IHistoryRepository>(history.Object)
            .WithSingleton<IFileSystem>(fileSystem.Object));
        using var cancellation = new CancellationTokenSource();
        var controller = _provider.GetRequiredService<LibraryController>();

        // When
        var deletion = controller.BulkDeleteAudiobooks(
            new LibraryController.BulkDeleteRequest { Ids = [audiobookId] },
            cancellation.Token);
        await preflightStarted.Task;
        cancellation.Cancel();
        releasePreflight.SetResult(audiobook);

        // Then
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion);
        repository.Verify(service => service.DeleteByIdAsync(audiobookId), Times.Never);
        imageCache.VerifyNoOtherCalls();
        history.VerifyNoOtherCalls();
        fileSystem.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BulkDelete_RequestCanceledAfterCommitBoundary_RemainsSuccessful()
    {
        // Given
        const int audiobookId = 4203;
        var audiobook = new Audiobook
        {
            Id = audiobookId,
            Title = "Committed bulk delete cancellation"
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
        var imageCache = new Mock<IImageCacheService>(MockBehavior.Strict);
        var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
        history.Setup(service => service.AddAsync(
                It.Is<History>(entry =>
                    entry.AudiobookId == audiobookId
                    && entry.EventType == "Deleted"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((History entry, CancellationToken _) => entry);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        Init(services => services
            .WithSingleton<IAudiobookRepository>(repository.Object)
            .WithSingleton<IImageCacheService>(imageCache.Object)
            .WithSingleton<IHistoryRepository>(history.Object)
            .WithSingleton<IFileSystem>(fileSystem.Object));

        // When
        var result = await _provider.GetRequiredService<LibraryController>()
            .BulkDeleteAudiobooks(
                new LibraryController.BulkDeleteRequest { Ids = [audiobookId] },
                cancellation.Token);

        // Then
        Assert.IsType<OkObjectResult>(result);
        Assert.True(cancellation.IsCancellationRequested);
        repository.Verify(service => service.DeleteByIdAsync(audiobookId), Times.Once);
        history.Verify(service => service.AddAsync(
            It.Is<History>(entry => entry.AudiobookId == audiobookId),
            It.IsAny<CancellationToken>()), Times.Once);
        imageCache.VerifyNoOtherCalls();
        fileSystem.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BulkDelete_RequestCanceledAfterFirstCommit_ReturnsCommittedPartialResultAndDoesNotDeleteNext()
    {
        // Given
        const int firstId = 4204;
        const int secondId = 4205;
        var first = new Audiobook
        {
            Id = firstId,
            Title = "Committed first bulk delete"
        };
        using var cancellation = new CancellationTokenSource();
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetForUpdateSnapshotAsync(
                firstId,
                cancellation.Token))
            .ReturnsAsync(first);
        repository.Setup(service => service.DeleteByIdAsync(firstId))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            });
        var imageCache = new Mock<IImageCacheService>(MockBehavior.Strict);
        var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
        history.Setup(service => service.AddAsync(
                It.Is<History>(entry =>
                    entry.AudiobookId == firstId
                    && entry.EventType == "Deleted"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((History entry, CancellationToken _) => entry);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        Init(services => services
            .WithSingleton<IAudiobookRepository>(repository.Object)
            .WithSingleton<IImageCacheService>(imageCache.Object)
            .WithSingleton<IHistoryRepository>(history.Object)
            .WithSingleton<IFileSystem>(fileSystem.Object));

        // When
        var result = await _provider.GetRequiredService<LibraryController>()
            .BulkDeleteAudiobooks(
                new LibraryController.BulkDeleteRequest
                {
                    Ids = [firstId, secondId]
                },
                cancellation.Token);

        // Then
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        Assert.Equal(
            1,
            Assert.IsType<int>(payload.GetType().GetProperty("deletedCount")!.GetValue(payload)));
        var deletedIds = Assert.IsAssignableFrom<IEnumerable<int>>(
            payload.GetType().GetProperty("ids")!.GetValue(payload));
        Assert.Equal([firstId], deletedIds);
        repository.Verify(service => service.DeleteByIdAsync(firstId), Times.Once);
        repository.Verify(service => service.GetForUpdateSnapshotAsync(
            secondId,
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(service => service.DeleteByIdAsync(secondId), Times.Never);
    }

    [Fact]
    public async Task BulkDelete_CanceledImageCleanupAfterCommit_RemainsSuccessful()
    {
        // Given
        const int audiobookId = 4202;
        var audiobook = new Audiobook
        {
            Id = audiobookId,
            Title = "Committed bulk delete",
            Asin = "BULKCANCEL"
        };
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetForUpdateSnapshotAsync(
                audiobookId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(audiobook);
        repository.Setup(service => service.DeleteByIdAsync(audiobookId))
            .ReturnsAsync(true);
        var imageCache = new Mock<IImageCacheService>(MockBehavior.Strict);
        imageCache.Setup(service => service.GetCachedImagePathAsync(audiobook.Asin))
            .ThrowsAsync(new TaskCanceledException("Injected post-commit cleanup cancellation."));
        var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
        history.Setup(service => service.AddAsync(
                It.Is<History>(entry =>
                    entry.AudiobookId == audiobookId
                    && entry.EventType == "Deleted"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((History entry, CancellationToken _) => entry);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        Init(services => services
            .WithSingleton<IAudiobookRepository>(repository.Object)
            .WithSingleton<IImageCacheService>(imageCache.Object)
            .WithSingleton<IHistoryRepository>(history.Object)
            .WithSingleton<IFileSystem>(fileSystem.Object));

        // When
        var result = await _provider.GetRequiredService<LibraryController>()
            .BulkDeleteAudiobooks(new LibraryController.BulkDeleteRequest
            {
                Ids = [audiobookId]
            });

        // Then
        Assert.IsType<OkObjectResult>(result);
        repository.Verify(service => service.DeleteByIdAsync(audiobookId), Times.Once);
        imageCache.Verify(service => service.GetCachedImagePathAsync(audiobook.Asin), Times.Once);
        history.Verify(service => service.AddAsync(
            It.Is<History>(entry => entry.AudiobookId == audiobookId),
            It.IsAny<CancellationToken>()), Times.Once);
        fileSystem.VerifyNoOtherCalls();
    }
}
