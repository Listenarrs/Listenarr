namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class MoveJobProcessorTests
{
    [Fact]
    public async Task ProcessJobAsync_AcquiresGlobalMutationBoundaryBeforeAudiobookLock()
    {
        var source = FileService.GetTempDirectory("move-processor-lock-order-src");
        await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-processor-lock-order-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Move lock order",
            BasePath = source
        });
        var (_, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var events = new List<string>();
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(
            _provider,
            new RecordingFilesystemMutationCoordinator(events),
            new RecordingAudiobookOperationCoordinator(events));

        await processor.ProcessJobAsync(job, CancellationToken.None);

        Assert.Equal(
            ["global-enter", "audiobook-enter", "audiobook-exit", "global-exit"],
            events);
    }

    private sealed class RecordingFilesystemMutationCoordinator(
        List<string> events) : IFilesystemMutationCoordinator
    {
        public async Task ExecuteExclusiveAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            events.Add("global-enter");
            await operation(cancellationToken);
            events.Add("global-exit");
        }

        public async Task<T> ExecuteExclusiveAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            events.Add("global-enter");
            var result = await operation(cancellationToken);
            events.Add("global-exit");
            return result;
        }
    }

    private sealed class RecordingAudiobookOperationCoordinator(
        List<string> events) : IAudiobookOperationCoordinator
    {
        public Task ExecuteExclusiveAsync(
            int audiobookId,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(operation, cancellationToken);

        public Task<T> ExecuteExclusiveAsync<T>(
            int audiobookId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(operation, cancellationToken);

        public Task ExecuteExclusiveAsync(
            IEnumerable<int> audiobookIds,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(operation, cancellationToken);

        public Task<T> ExecuteExclusiveAsync<T>(
            IEnumerable<int> audiobookIds,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(operation, cancellationToken);

        private async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            events.Add("audiobook-enter");
            await operation(cancellationToken);
            events.Add("audiobook-exit");
        }

        private async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            events.Add("audiobook-enter");
            var result = await operation(cancellationToken);
            events.Add("audiobook-exit");
            return result;
        }
    }
}
