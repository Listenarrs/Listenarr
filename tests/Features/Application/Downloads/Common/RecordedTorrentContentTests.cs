using System.Text.Json;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.DownloadClients;

namespace Listenarr.Tests.Features.Application.Downloads.Common
{
    /// <summary>
    /// A monitor poll records the files a torrent's client lists under its content path, so a
    /// removed torrent is later recovered from those files and not from whatever its folder holds.
    /// </summary>
    [Trait("Name", "RecordedTorrentContentTests")]
    [Trait("Category", "DownloadClientGateway")]
    public class RecordedTorrentContentTests : BaseTests
    {
        private const string TorrentHash = "0123456789abcdef0123456789abcdef01234567";

        private IDownloadClientGateway gateway = null!;
        private DownloadCLientAdapterMock adapter = null!;
        private DownloadClientConfiguration client = null!;
        private string contentDir = null!;

        public override async Task InitializeAsync()
        {
            gateway = _provider.GetRequiredService<IDownloadClientGateway>();
            client = new DownloadClientConfigurationBuilder()
                .WithType("mock")
                .Build();
            adapter = (DownloadCLientAdapterMock)((DownloadClientGateway)gateway).ResolveAdapter(client);

            // The folder holds the listed file and an audio file the torrent does not list.
            contentDir = Path.Join(FileService.GetTempDirectory("recorded-content"), "BookFolder");
            Directory.CreateDirectory(contentDir);
            await FileService.GetFileAsync(contentDir, "book.m4b");
            await FileService.GetFileAsync(contentDir, "stray.m4b");
        }

        [Fact]
        public async Task FetchDownloadsAsync_ClientListsFiles_RecordsTheirNamesUnderTheContentPath()
        {
            adapter.QueueItemsMock = [PolledItem(Path.Join(contentDir, "book.m4b"))];

            var polled = await gateway.FetchDownloadsAsync(client, [Torrent()]);

            Assert.Equal("""["book.m4b"]""", polled.Single().GetMetadataString("ClientContentFiles"));
        }

        [Fact]
        public async Task FetchDownloadsAsync_ContentPathWithoutClientFileList_DoesNotRecordTheFolderContents()
        {
            // A poll that reports a folder and no file list, as Transmission's does. The gateway
            // fills the item's files from that folder, which also holds the unlisted file.
            adapter.QueueItemsMock = [PolledItem()];
            var download = Torrent();
            download.SetMetadata("ClientContentFiles", """["book.m4b"]""");

            var polled = await gateway.FetchDownloadsAsync(client, [download]);

            Assert.Null(polled.Single().GetMetadataString("ClientContentFiles"));
        }

        [Fact]
        public async Task FetchDownloadsAsync_PollWithoutContentPath_KeepsTheRecordedList()
        {
            adapter.QueueItemsMock = [new QueueItemBuilder().WithId(TorrentHash).WithStatus("completed").Build()];
            var download = Torrent();
            download.SetMetadata("ClientContentPath", contentDir);
            download.SetMetadata("ClientContentFiles", """["book.m4b"]""");

            var polled = await gateway.FetchDownloadsAsync(client, [download]);

            Assert.Equal(contentDir, polled.Single().GetMetadataString("ClientContentPath"));
            Assert.Equal("""["book.m4b"]""", polled.Single().GetMetadataString("ClientContentFiles"));
        }

        [Theory]
        [InlineData(new[] { "book.m4b" }, new[] { "book.m4b" })]
        [InlineData(new[] { "book.m4b", "part2.m4b" }, new string[0])]
        public async Task GetQueueItemAsync_RemovedTorrent_ReturnsOnlyRecordedFilesThatAreAllOnDisk(
            string[] recordedFiles,
            string[] expectedFiles)
        {
            // The client no longer lists the torrent, so it reports neither files nor a content path.
            adapter.QueueItemMock = new QueueItem { SourceFiles = [] };
            var download = new DownloadBuilder()
                .WithExternalId(TorrentHash)
                .WithTorrentHash(TorrentHash)
                .WithPath(Path.GetDirectoryName(contentDir)!)
                .Build();
            download.SetMetadata("ClientContentPath", contentDir);
            download.SetMetadata("ClientContentFiles", JsonSerializer.Serialize(recordedFiles));

            var item = await gateway.GetQueueItemAsync(client, download, new QueueItem());

            Assert.Equal(expectedFiles.Select(name => Path.Join(contentDir, name)), item.SourceFiles);
        }

        [Fact]
        public async Task GetQueueItemAsync_RemovedTorrentWithRecordedList_DoesNotImportAFileReplacingItsFolder()
        {
            // The recorded folder was removed with its torrent and a regular audio file now sits at
            // the folder's path. The recorded list names a file under that folder, so it decides.
            var replacedPath = Path.Join(Path.GetDirectoryName(contentDir)!, "Replaced.m4b");
            await FileService.GetFileAsync(Path.GetDirectoryName(replacedPath)!, "Replaced.m4b");

            var item = await GetRemovedItemAsync(replacedPath, """["Chapter.m4b"]""");

            Assert.Empty(item.SourceFiles);
            Assert.Null(item.ContentPath);
        }

        [Fact]
        public async Task GetQueueItemAsync_RemovedTorrentRecordedAsSingleFile_ReturnsThatFile()
        {
            // A single-file torrent is recorded without a list; its content path is the file.
            var singleFile = Path.Join(contentDir, "book.m4b");

            var item = await GetRemovedItemAsync(singleFile, null);

            Assert.Equal([singleFile], item.SourceFiles);
        }

        [Fact]
        public async Task GetQueueItemAsync_RemovedTorrentWithNestedRecordedFile_ReturnsIt()
        {
            var discDir = Directory.CreateDirectory(Path.Join(contentDir, "Disc 1")).FullName;
            await FileService.GetFileAsync(discDir, "track.m4b");

            var item = await GetRemovedItemAsync(contentDir, """["Disc 1/track.m4b"]""");

            Assert.Equal([Path.Join(contentDir, "Disc 1", "track.m4b")], item.SourceFiles);
        }

        private async Task<QueueItem> GetRemovedItemAsync(string recordedPath, string? recordedFilesJson)
        {
            adapter.QueueItemMock = new QueueItem { SourceFiles = [] };
            var download = new DownloadBuilder()
                .WithExternalId(TorrentHash)
                .WithTorrentHash(TorrentHash)
                .WithPath(Path.GetDirectoryName(contentDir)!)
                .Build();
            download.SetMetadata("ClientContentPath", recordedPath);
            if (recordedFilesJson != null)
            {
                download.SetMetadata("ClientContentFiles", recordedFilesJson);
            }

            return await gateway.GetQueueItemAsync(client, download, new QueueItem());
        }

        private QueueItem PolledItem(params string[] listedFiles)
        {
            var builder = new QueueItemBuilder()
                .WithId(TorrentHash)
                .WithRemotePath(Path.GetDirectoryName(contentDir)!)
                .WithContentPath(contentDir)
                .WithStatus("completed");
            foreach (var file in listedFiles)
            {
                builder.WithSourceFile(file);
            }

            return builder.Build();
        }

        private static Download Torrent() => new DownloadBuilder()
            .WithExternalId(TorrentHash)
            .WithTorrentHash(TorrentHash)
            .Build();
    }
}
