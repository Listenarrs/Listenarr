using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Mocks
{
    public class DownloadCLientAdapterMock(
        IDownloadRepository downloadRepository) : IDownloadClientAdapter
    {
        public static readonly string RemotePath = FileUtils.GetAbsolutePath("downloads", "complete", "audiobooks");
        public string ClientId => "mock";

        public string ClientType => "mock";

        public DownloadProtocol Protocol => DownloadProtocol.Torrent;
        public QueueItem QueueItemMock { get; set; } = null;

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            var download = await downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithPath(RemotePath)
                .Build());

            // FIXME: Currently, the IDownloadClientAdapter returns specific client ID's, this should change to return uniformized Download instead
            return download.Id;
        }

        public Task<DownloadClientItem> GetImportItemAsync(DownloadClientConfiguration client, DownloadClientItem item, DownloadClientItem? previousAttempt = null, CancellationToken ct = default)
        {
            throw new NotImplementedException();
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

        public Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var path1 = FileUtils.GetAbsolutePath(RemotePath, "random title");
            var path2 = FileUtils.GetAbsolutePath(RemotePath, "random title two");

            List<QueueItem> result = [
                new QueueItemBuilder()
                    .WithRemotePath(path1)
                    .WithSourceFile(Path.Join(path1, "file1.mp3"))
                    .WithSourceFile(Path.Join(path1, "file2.mp3"))
                    .WithSourceFile(Path.Join(path1, "file3.mp3"))
                    .Build(),
                new QueueItemBuilder()
                    .WithRemotePath(path2)
                    .WithSourceFile(Path.Join(path2, "file1.mp3"))
                    .WithSourceFile(Path.Join(path2, "file10.mp3"))
                    .WithSourceFile(Path.Join(path2, "file5.mp3"))
                    .WithSourceFile(Path.Join(path2, "file.nfo"))
                    .WithSourceFile(Path.Join(path2, "helloworld.txt"))
                    .Build()
            ];
            return result;
        }

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            return (true, "mock");
        }

        public async Task<List<Download>> FetchDownloadsAsync(DownloadClientConfiguration client, List<Download> downloads, CancellationToken cancellationToken = default)
        {
            // When no downloads are given, this mock returns hardcoded data
            if (downloads.Count <= 0)
            {
                var path1 = FileUtils.GetAbsolutePath(RemotePath, "random title");
                var path2 = FileUtils.GetAbsolutePath(RemotePath, "random title two");

                List<Download> results = [
                    new DownloadBuilder()
                        .WithPath(path1)
                        .Build(),
                    new DownloadBuilder()
                        .WithPath(path2)
                        .Build()
                ];

                foreach (Download download in results)
                {
                    await downloadRepository.AddAsync(download);
                }

                return results;
            }

            // Otherwise, simulate progress on given downloads
            foreach (Download download in downloads)
            {
                download.Progress += 10;
                if (download.Progress >= 100)
                {
                    download.Completed();
                }
            }

            return downloads;
        }
    }
}
