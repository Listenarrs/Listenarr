using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Files;

[Trait("Name", "AudiobookFileTransferServiceCoordinationTests")]
[Trait("Category", "Application")]
public sealed class AudiobookFileTransferServiceCoordinationTests : BaseTests
{
    private static AudiobookFileTransferService CreateService(
        IAudiobookRepository audiobookRepository,
        IAudiobookFileRepository fileRepository,
        IMoveQueueService moveQueueService,
        IFilesystemMutationCoordinator? filesystemMutationCoordinator = null,
        IAudiobookOperationCoordinator? audiobookOperationCoordinator = null) =>
        new(
            audiobookRepository,
            fileRepository,
            Mock.Of<IAudiobookFilePathIdentityResolver>(),
            filesystemMutationCoordinator ?? new FilesystemMutationCoordinator(),
            audiobookOperationCoordinator ?? new AudiobookOperationCoordinator(),
            moveQueueService,
            Mock.Of<IRenameService>(),
            Mock.Of<IRootFolderService>(),
            Mock.Of<IConfigurationService>(),
            Mock.Of<IFileSystem>(),
            NullLogger<AudiobookFileTransferService>.Instance);

    [Fact]
    public async Task TransferAsync_FencesOnMoveRecovery_ForBothAudiobooks_BeforeTouchingRows()
    {
        var moveQueueService = new Mock<IMoveQueueService>(MockBehavior.Strict);
        moveQueueService.Setup(service => service.EnsureFilesystemMutationAllowedAsync(
                7,
                It.IsAny<CancellationToken>(),
                false))
            .Returns(Task.CompletedTask);
        moveQueueService.Setup(service => service.EnsureFilesystemMutationAllowedAsync(
                9,
                It.IsAny<CancellationToken>(),
                false))
            .ThrowsAsync(new ApplicationConflictException(
                "move_recovery_required",
                "An interrupted move still owns this audiobook's filesystem state."));
        var audiobookRepository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var fileRepository = new Mock<IAudiobookFileRepository>(MockBehavior.Strict);
        var service = CreateService(
            audiobookRepository.Object,
            fileRepository.Object,
            moveQueueService.Object);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.TransferAsync(new AudiobookFileTransferRequest(7, 9)));

        Assert.Equal("move_recovery_required", exception.Code);
        audiobookRepository.VerifyNoOtherCalls();
        fileRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TransferAsync_AcquiresGlobalBoundary_ThenBothAudiobookLocks_InKeyOrder()
    {
        var events = new List<string>();
        var moveQueueService = new Mock<IMoveQueueService>();
        var audiobookRepository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        audiobookRepository.Setup(repository => repository.GetByIdSnapshotAsync(
                12,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Audiobook?)null);
        var fileRepository = new Mock<IAudiobookFileRepository>(MockBehavior.Strict);
        var service = CreateService(
            audiobookRepository.Object,
            fileRepository.Object,
            moveQueueService.Object,
            new RecordingFilesystemMutationCoordinator(events),
            new RecordingAudiobookOperationCoordinator(events));

        var result = await service.TransferAsync(new AudiobookFileTransferRequest(12, 3));

        Assert.Equal(AudiobookFileTransferStatus.SourceNotFound, result.Status);
        Assert.Equal(
            ["global:enter", "audiobooks:3,12:enter", "audiobooks:3,12:exit", "global:exit"],
            events);
        moveQueueService.Verify(
            queue => queue.EnsureFilesystemMutationAllowedAsync(12, It.IsAny<CancellationToken>(), false),
            Times.Once);
        moveQueueService.Verify(
            queue => queue.EnsureFilesystemMutationAllowedAsync(3, It.IsAny<CancellationToken>(), false),
            Times.Once);
        fileRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TransferAsync_RejectsSameAudiobook_WithoutLocking()
    {
        var events = new List<string>();
        var service = CreateService(
            Mock.Of<IAudiobookRepository>(MockBehavior.Strict),
            Mock.Of<IAudiobookFileRepository>(MockBehavior.Strict),
            Mock.Of<IMoveQueueService>(MockBehavior.Strict),
            new RecordingFilesystemMutationCoordinator(events),
            new RecordingAudiobookOperationCoordinator(events));

        var result = await service.TransferAsync(new AudiobookFileTransferRequest(5, 5));

        Assert.Equal(AudiobookFileTransferStatus.SameAudiobook, result.Status);
        Assert.Empty(events);
    }

    private sealed class RecordingFilesystemMutationCoordinator(
        List<string> events) : IFilesystemMutationCoordinator
    {
        public async Task ExecuteExclusiveAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            events.Add("global:enter");
            try
            {
                await operation(cancellationToken);
            }
            finally
            {
                events.Add("global:exit");
            }
        }

        public async Task<T> ExecuteExclusiveAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            events.Add("global:enter");
            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                events.Add("global:exit");
            }
        }
    }

    private sealed class RecordingAudiobookOperationCoordinator(
        List<string> events) : IAudiobookOperationCoordinator
    {
        public Task ExecuteExclusiveAsync(
            int audiobookId,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

        public Task<T> ExecuteExclusiveAsync<T>(
            int audiobookId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

        public async Task ExecuteExclusiveAsync(
            IEnumerable<int> audiobookIds,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            var key = string.Join(',', audiobookIds.Distinct().Order());
            events.Add($"audiobooks:{key}:enter");
            try
            {
                await operation(cancellationToken);
            }
            finally
            {
                events.Add($"audiobooks:{key}:exit");
            }
        }

        public async Task<T> ExecuteExclusiveAsync<T>(
            IEnumerable<int> audiobookIds,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            var key = string.Join(',', audiobookIds.Distinct().Order());
            events.Add($"audiobooks:{key}:enter");
            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                events.Add($"audiobooks:{key}:exit");
            }
        }
    }
}
