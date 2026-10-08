using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Mocks
{
    public class DownloadCLientAdapterMock(
        IDownloadRepository downloadRepository) : IDownloadClientAdapter
    {
        public static readonly string RemotePath = FileUtils.GetAbsolutePath("downloads", "complete", "audiobooks");

        public string ClientType => "mock";

        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        public List<DownloadProtocol> Protocols => [
            DownloadProtocol.Torrent,
            DownloadProtocol.Usenet
        ];

        public QueueItem QueueItemMock { get; set; } = null;
        public List<QueueItem>? QueueItemsMock { get; set; }
        public List<string>? LastRequestedQueueIds { get; private set; }
        public bool LastQueueRequestWasFullSnapshot { get; private set; }
        public int FullSnapshotQueueRequestCount { get; private set; }
        public int FilteredQueueRequestCount { get; private set; }
        public Exception? FilteredQueueException { get; set; }
        private int CurrentProgress = 0;

        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            var download = await downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithPath(RemotePath)
                .Build());

            // FIXME: Currently, the IDownloadClientAdapter returns specific client ID's, this should change to return uniformized Download instead
            return new DownloadClientSubmissionResult(download.Id);
        }

        public async Task<QueueItem> GetImportItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, QueueItem? previousAttempt = null, CancellationToken ct = default)
        {
            if (QueueItemMock != null)
            {
                return QueueItemMock;
            }

            var path = FileUtils.GetAbsolutePath(RemotePath, "hello", "world", "running out of ideas", "here");
            return new QueueItemBuilder()
                .WithRemotePath(path)
                .WithSourceFile(Path.Join(path, "chapter1.mp3"))
                .WithSourceFile(Path.Join(path, "file2.mp3"))
                .WithSourceFile(Path.Join(path, "file3-chapter6.m4b"))
                .WithSourceFile(Path.Join(path, "very randomness.wow"))
                .Build();
        }

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            LastRequestedQueueIds = null;
            LastQueueRequestWasFullSnapshot = true;
            FullSnapshotQueueRequestCount++;
            return Task.FromResult(BuildQueueItems(advanceProgress: false));
        }

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
        {
            LastRequestedQueueIds = [.. ids];
            LastQueueRequestWasFullSnapshot = false;
            FilteredQueueRequestCount++;

            if (FilteredQueueException != null)
            {
                throw FilteredQueueException;
            }

            var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(BuildQueueItems(advanceProgress: true)
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && idSet.Contains(item.Id))
                .ToList());
        }

        private List<QueueItem> BuildQueueItems(bool advanceProgress)
        {
            var path1 = FileUtils.GetAbsolutePath(RemotePath, "random title");
            var path2 = FileUtils.GetAbsolutePath(RemotePath, "random title two");

            // Simulate monitor progress only for targeted update polling. Full
            // queue snapshots are display/reconciliation reads and should not
            // advance the mock download lifecycle.
            if (advanceProgress)
            {
                CurrentProgress += 10;
            }

            if (QueueItemsMock != null)
            {
                return QueueItemsMock;
            }

            return [
                new QueueItemBuilder()
                    .WithId("1")
                    .WithRemotePath(path1)
                    .WithContentPath(path1)
                    .WithSourceFile(Path.Join(path1, "file1.mp3"))
                    .WithSourceFile(Path.Join(path1, "file2.mp3"))
                    .WithSourceFile(Path.Join(path1, "file3.mp3"))
                    .WithProgress(CurrentProgress)
                    .WithStatus(CurrentProgress >= 100 ? "completed" : "downloading")
                    .Build(),
                new QueueItemBuilder()
                    .WithId("2")
                    .WithRemotePath(path2)
                    .WithContentPath(path2)
                    .WithSourceFile(Path.Join(path2, "file1.mp3"))
                    .WithSourceFile(Path.Join(path2, "file10.mp3"))
                    .WithSourceFile(Path.Join(path2, "file5.mp3"))
                    .WithSourceFile(Path.Join(path2, "file.nfo"))
                    .WithSourceFile(Path.Join(path2, "helloworld.txt"))
                    .WithProgress(CurrentProgress)
                    .WithStatus(CurrentProgress >= 100 ? "completed" : "downloading")
                    .Build()
            ];
        }

        public Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            if (ItemsException != null) throw ItemsException;
            return Task.FromResult(Items);
        }

        public List<DownloadClientItem> Items { get; set; } = [];
        public Exception? ItemsException { get; set; }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            return (true, "mock");
        }
    }
}
