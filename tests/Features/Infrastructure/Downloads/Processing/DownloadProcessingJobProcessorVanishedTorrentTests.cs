using System.Net;
using System.Text;
using System.Text.Json;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
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

        [Theory]
        [InlineData(HttpStatusCode.NotFound, "")]
        [InlineData(HttpStatusCode.OK, "[]")]
        public async Task ProcessQueueAsync_QbittorrentItemRemovedWithPayloadOnDisk_PublishesOnlyItsOwnContent(
            HttpStatusCode filesStatus,
            string filesBody)
        {
            using var scenario = await ArrangeAsync(filesStatus, filesBody, recordContentPath: true);

            await scenario.RunAsync();

            Assert.True(File.Exists(Path.Join(scenario.DestRoot, "book.m4b")), scenario.JobState);
            Assert.False(File.Exists(Path.Join(scenario.DestRoot, "other.m4b")));
        }

        [Fact]
        public async Task ProcessQueueAsync_QbittorrentItemRemovedWithoutRecordedContentPath_DoesNotGuessFromSaveFolder()
        {
            using var scenario = await ArrangeAsync(HttpStatusCode.NotFound, string.Empty, recordContentPath: false);

            await scenario.RunAsync();

            Assert.False(File.Exists(Path.Join(scenario.DestRoot, "book.m4b")));
            Assert.False(File.Exists(Path.Join(scenario.DestRoot, "other.m4b")));
        }

        [Fact]
        public async Task ProcessQueueAsync_QbittorrentItemStillListed_PublishesFromClientFileList()
        {
            using var scenario = await ArrangeAsync(HttpStatusCode.OK, """[{"name":"BookFolder/book.m4b"}]""", recordContentPath: false);

            await scenario.RunAsync();

            Assert.True(File.Exists(Path.Join(scenario.DestRoot, "book.m4b")), scenario.JobState);
            Assert.False(File.Exists(Path.Join(scenario.DestRoot, "other.m4b")));
        }

        private async Task<Scenario> ArrangeAsync(HttpStatusCode filesStatus, string filesBody, bool recordContentPath)
        {
            // The save folder is shared with another torrent's payload, as a client's download folder is.
            var saveDir = FileService.GetTempDirectory("dl-save");
            var contentDir = Path.Join(saveDir, "BookFolder");
            Directory.CreateDirectory(contentDir);
            await FileService.GetFileAsync(contentDir, "book.m4b");
            await FileService.GetFileAsync(saveDir, "other.m4b");
            var destRoot = FileService.GetTempDirectory("dl-dest");

            var qbittorrent = FakeQbittorrent.Start(filesStatus, filesBody, JsonSerializer.Serialize(new { save_path = saveDir }));
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
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destRoot)
                .Build());
            var download = new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(saveDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithTorrentHash(TorrentHash)
                .WithAudiobook(audiobook)
                .Build();
            if (recordContentPath)
            {
                download.SetMetadata("ClientContentPath", contentDir);
            }

            await _downloadRepository.AddAsync(download);
            var jobId = await _provider.GetRequiredService<IDownloadProcessingJobService>().EnqueueAsync(download);
            return new Scenario(this, qbittorrent, jobId, destRoot);
        }

        private sealed class Scenario(
            DownloadProcessingJobProcessorVanishedTorrentTests owner,
            FakeQbittorrent qbittorrent,
            string jobId,
            string destRoot) : IDisposable
        {
            public string DestRoot { get; } = destRoot;

            public string JobState { get; private set; } = string.Empty;

            public async Task RunAsync()
            {
                await owner._provider.GetRequiredService<DownloadProcessingJobProcessor>().ProcessQueueAsync(CancellationToken.None);
                var job = await owner._downloadProcessingJobRepository.GetByIdAsync(jobId);
                JobState = $"job {job?.Status}: {job?.ErrorMessage} {string.Join(" / ", job?.ProcessingLog ?? [])}";
            }

            public void Dispose() => qbittorrent.Dispose();
        }

        private sealed class FakeQbittorrent : IDisposable
        {
            private readonly HttpListener _listener;

            private FakeQbittorrent(HttpListener listener, int port)
            {
                _listener = listener;
                Port = port;
            }

            public int Port { get; }

            public static FakeQbittorrent Start(HttpStatusCode filesStatus, string filesBody, string propertiesBody)
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

                    var fake = new FakeQbittorrent(listener, port);
                    _ = fake.ServeAsync(filesStatus, filesBody, propertiesBody);
                    return fake;
                }

                throw new InvalidOperationException("No free loopback port for the fake qBittorrent");
            }

            private async Task ServeAsync(HttpStatusCode filesStatus, string filesBody, string propertiesBody)
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
                        : path.EndsWith("/torrents/files") ? (filesStatus, filesBody)
                        : path.EndsWith("/torrents/properties") ? (HttpStatusCode.OK, propertiesBody)
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
