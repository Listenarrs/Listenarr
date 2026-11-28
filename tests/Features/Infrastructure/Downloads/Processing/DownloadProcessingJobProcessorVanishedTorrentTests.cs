using System.Net;
using System.Text;
using System.Text.Json;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.DownloadClients;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Processing
{
    /// <summary>
    /// A torrent the client no longer lists has no file list to ask for. When its payload is
    /// still in the download folder, the import has to publish it from there.
    /// </summary>
    [Trait("Name", "DownloadProcessingJobProcessorVanishedTorrentTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadProcessingJobProcessorVanishedTorrentTests : BaseTests
    {
        private const string TorrentHash = "0123456789abcdef0123456789abcdef01234567";

        private const string ListedBookFile = """[{"name":"BookFolder/book.m4b","priority":1}]""";

        [Theory]
        [InlineData(HttpStatusCode.NotFound, "")]
        [InlineData(HttpStatusCode.OK, "[]")]
        public async Task ProcessQueueAsync_QbittorrentItemRemovedAfterPoll_PublishesOnlyTheFilesItListed(
            HttpStatusCode filesStatus,
            string filesBody)
        {
            // The recorded subfolder also holds audio the torrent never listed.
            using var scenario = await ArrangeAsync(ListedBookFile);
            await scenario.PollAsync();
            scenario.RemoveFromClient(filesStatus, filesBody);

            await scenario.RunAsync();

            Assert.Equal(["book.m4b"], scenario.ImportedFileNames());
            Assert.Equal(ProcessingJobStatus.Completed, scenario.Status);
        }

        [Fact]
        public async Task ProcessQueueAsync_SingleFileQbittorrentItemRemovedAfterPoll_PublishesThatFile()
        {
            using var scenario = await ArrangeAsync("""[{"name":"single.m4b","priority":1}]""");
            await scenario.PollAsync();
            scenario.RemoveFromClient(HttpStatusCode.NotFound, string.Empty);

            await scenario.RunAsync();

            Assert.Equal(["single.m4b"], scenario.ImportedFileNames());
            Assert.Equal(ProcessingJobStatus.Completed, scenario.Status);
        }

        [Fact]
        public async Task ProcessQueueAsync_QbittorrentItemRemovedWithOnlyItsFolderRecorded_LeavesImportBlocked()
        {
            // A completion recorded before file lists were kept has its content path and nothing else.
            using var scenario = await ArrangeAsync(ListedBookFile, recordContentPath: true);
            scenario.RemoveFromClient(HttpStatusCode.NotFound, string.Empty);

            await scenario.RunAsync();

            Assert.Empty(scenario.ImportedFileNames());
            Assert.NotEqual(ProcessingJobStatus.Completed, scenario.Status);
        }

        [Theory]
        [InlineData("../other.m4b")]
        [InlineData("BookFolder/../../other.m4b")]
        [InlineData(null)]
        public async Task ProcessQueueAsync_RecordedFileNameOutsideItsFolder_LeavesImportBlocked(string? escapingName)
        {
            using var scenario = await ArrangeAsync(ListedBookFile, recordContentPath: true,
                recordedFiles: saveDir => ["book.m4b", escapingName ?? Path.Join(saveDir, "other.m4b")]);
            scenario.RemoveFromClient(HttpStatusCode.NotFound, string.Empty);

            await scenario.RunAsync();

            Assert.Empty(scenario.ImportedFileNames());
            Assert.NotEqual(ProcessingJobStatus.Completed, scenario.Status);
        }

        [Fact]
        public async Task ProcessQueueAsync_RecordedFileMissingFromDisk_LeavesImportBlocked()
        {
            using var scenario = await ArrangeAsync(ListedBookFile, recordContentPath: true,
                recordedFiles: _ => ["book.m4b", "part2.m4b"]);
            scenario.RemoveFromClient(HttpStatusCode.NotFound, string.Empty);

            await scenario.RunAsync();

            Assert.Empty(scenario.ImportedFileNames());
            Assert.NotEqual(ProcessingJobStatus.Completed, scenario.Status);
        }

        [Fact]
        public async Task ProcessQueueAsync_QbittorrentItemRemovedWithoutRecordedContentPath_DoesNotGuessFromSaveFolder()
        {
            using var scenario = await ArrangeAsync(ListedBookFile);
            scenario.RemoveFromClient(HttpStatusCode.NotFound, string.Empty);

            await scenario.RunAsync();

            Assert.Empty(scenario.ImportedFileNames());
        }

        [Fact]
        public async Task ProcessQueueAsync_QbittorrentItemRemovedWithContentPathEqualToSaveFolder_ImportsNothing()
        {
            // A multi-file torrent without a common root reports the save folder as its content path.
            using var scenario = await ArrangeAsync(ListedBookFile, recordContentPath: true, contentPathIsSaveFolder: true);
            scenario.RemoveFromClient(HttpStatusCode.NotFound, string.Empty);

            await scenario.RunAsync();

            Assert.Empty(scenario.ImportedFileNames());
        }

        [Fact]
        public async Task ProcessQueueAsync_RemovedItemWithoutTorrentHash_DoesNotRecoverFromRecordedPath()
        {
            using var scenario = await ArrangeAsync(ListedBookFile, recordContentPath: true, isTorrent: false,
                recordedFiles: _ => ["book.m4b"]);
            scenario.RemoveFromClient(HttpStatusCode.NotFound, string.Empty);

            await scenario.RunAsync();

            Assert.Empty(scenario.ImportedFileNames());
        }

        [Fact]
        public async Task ProcessQueueAsync_QbittorrentItemStillListed_PublishesFromClientFileList()
        {
            using var scenario = await ArrangeAsync(ListedBookFile);

            await scenario.RunAsync();

            Assert.Equal(["book.m4b"], scenario.ImportedFileNames());
            Assert.Equal(ProcessingJobStatus.Completed, scenario.Status);
        }

        private async Task<Scenario> ArrangeAsync(
            string listedFiles,
            bool recordContentPath = false,
            bool isTorrent = true,
            bool contentPathIsSaveFolder = false,
            Func<string, string[]>? recordedFiles = null)
        {
            // The save folder is shared with another torrent's payload, as a client's download folder is,
            // and the torrent's own folder holds an audio file the torrent does not list.
            var saveDir = FileService.GetTempDirectory("dl-save");
            var contentDir = Path.Join(saveDir, "BookFolder");
            Directory.CreateDirectory(contentDir);
            await FileService.GetFileAsync(contentDir, "book.m4b");
            await FileService.GetFileAsync(contentDir, "stray.m4b");
            await FileService.GetFileAsync(saveDir, "other.m4b");
            await FileService.GetFileAsync(saveDir, "single.m4b");
            var destRoot = FileService.GetTempDirectory("dl-dest");

            var qbittorrent = FakeQbittorrent.Start(saveDir, listedFiles);
            _services.Replace(new ServiceDescriptor(typeof(IDownloadItemService), typeof(DownloadItemService), ServiceLifetime.Singleton));
            Init();
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destRoot)
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("127.0.0.1")
                .WithPort(qbittorrent.Port)
                .WithType("qbittorrent")
                .WithUsername("listenarr")
                .WithPassword("listenarr")
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destRoot)
                .Build());
            var download = new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(saveDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithAudiobook(audiobook)
                .Build();
            if (isTorrent)
            {
                download.SetMetadata("TorrentHash", TorrentHash);
            }

            if (recordContentPath)
            {
                download.SetMetadata("ClientContentPath", contentPathIsSaveFolder ? saveDir : contentDir);
            }

            if (recordedFiles != null)
            {
                download.SetMetadata("ClientContentFiles", JsonSerializer.Serialize(recordedFiles(saveDir)));
            }

            await _downloadRepository.AddAsync(download);
            var jobId = await _provider.GetRequiredService<IDownloadProcessingJobService>().EnqueueAsync(download);
            return new Scenario(this, qbittorrent, client, download.Id, jobId, destRoot, saveDir, listedFiles);
        }

        private sealed class Scenario(
            DownloadProcessingJobProcessorVanishedTorrentTests owner,
            FakeQbittorrent qbittorrent,
            DownloadClientConfiguration client,
            string downloadId,
            string jobId,
            string destRoot,
            string saveDir,
            string listedFiles) : IDisposable
        {
            public ProcessingJobStatus? Status { get; private set; }

            // Records what the client reports for the torrent, as one monitor poll does. The
            // monitor's requests go through the qBittorrent API mock; the import resolver's go to
            // the fake client below.
            public async Task PollAsync()
            {
                var api = owner._provider.GetRequiredService<QbittorrentApiMock>();
                api.InfoResponseOverride = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        hash = TorrentHash,
                        name = "BookFolder",
                        progress = 1.0,
                        size = 1000L,
                        downloaded = 1000L,
                        state = "stalledUP",
                        save_path = saveDir
                    }
                });
                api.FilesResponseOverride = listedFiles;
                var download = await owner._downloadRepository.GetByIdAsync(downloadId);
                var previous = download!.Clone();
                var polled = await owner._provider.GetRequiredService<IDownloadClientGateway>()
                    .FetchDownloadsAsync(client, [download]);
                Assert.True(await owner._downloadRepository.TryUpdateFromClientPollAsync(previous, polled.Single()));
            }

            public void RemoveFromClient(HttpStatusCode filesStatus, string filesBody) => qbittorrent.Remove(filesStatus, filesBody);

            public async Task RunAsync()
            {
                await owner._provider.GetRequiredService<DownloadProcessingJobProcessor>().ProcessQueueAsync(CancellationToken.None);
                var job = await owner._downloadProcessingJobRepository.GetByIdAsync(jobId);
                Status = job?.Status;
            }

            public string[] ImportedFileNames() => [.. Directory
                .GetFiles(destRoot, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)];

            public void Dispose() => qbittorrent.Dispose();
        }

        private sealed class FakeQbittorrent : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly string _propertiesBody;
            private (HttpStatusCode Status, string Body) _files;

            private FakeQbittorrent(HttpListener listener, int port, string saveDir, string listedFiles)
            {
                _listener = listener;
                Port = port;
                _propertiesBody = JsonSerializer.Serialize(new { save_path = saveDir });
                _files = (HttpStatusCode.OK, listedFiles);
            }

            public int Port { get; }

            public static FakeQbittorrent Start(string saveDir, string listedFiles)
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    var port = Random.Shared.Next(20000, 60000);
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    try
                    {
                        listener.Start();
                    }
                    catch (HttpListenerException)
                    {
                        listener.Close();
                        continue;
                    }

                    var fake = new FakeQbittorrent(listener, port, saveDir, listedFiles);
                    _ = fake.ServeAsync();
                    return fake;
                }

                throw new InvalidOperationException("No free loopback port for the fake qBittorrent");
            }

            // The client forgets the torrent, so its files request answers as for an unknown hash.
            public void Remove(HttpStatusCode filesStatus, string filesBody) => _files = (filesStatus, filesBody);

            private async Task ServeAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    var path = context.Request.Url!.AbsolutePath;
                    var (status, body) = path.EndsWith("/auth/login") ? (HttpStatusCode.OK, "Ok.")
                        : path.EndsWith("/torrents/files") ? _files
                        : path.EndsWith("/torrents/properties") ? (HttpStatusCode.OK, _propertiesBody)
                        : (HttpStatusCode.NotFound, string.Empty);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = (int)status;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }

            public void Dispose() => _listener.Close();
        }
    }
}
