using Listenarr.Domain.Downloads.Exceptions;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Application.Downloads.Common
{
    [Trait("Name", "DownloadClientGatewayTests")]
    [Trait("Category", "DownloadClientGateway")]
    public class DownloadClientGatewayTests : BaseTests
    {
        private readonly string localMapping = FileUtils.GetAbsolutePath("mnt", "wdelements", "downloads");
        private readonly string localPath = null!;

        private IDownloadClientGateway downloadClientGateway = null!;
        private DownloadClientConfiguration client = null!;

        public DownloadClientGatewayTests()
        {
            localPath = Path.Join(localMapping, "complete", "audiobooks");
        }

        public override async Task InitializeAsync()
        {
            downloadClientGateway = _provider.GetRequiredService<IDownloadClientGateway>();

            client = new DownloadClientConfigurationBuilder()
                .WithType("mock")
                .Build();

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath(FileUtils.GetAbsolutePath("downloads"))
                .WithLocalPath(localMapping)
                .Build());
        }

        private async Task IsValid(QueueItem item)
        {
            Assert.StartsWith(DownloadCLientAdapterMock.RemotePath, item.RemotePath);
            Assert.StartsWith(localPath, item.LocalPath);

            foreach (string path in item.SourceFiles)
            {
                Assert.StartsWith(localPath, path);
            }
        }

        [Fact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Make sure GetQueueItemAsync returns a list of items with path mapped")]
        public async Task GetQueueItemAsync()
        {
            var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());
            await IsValid(item);
        }

        [Fact]
        [Trait("Method", "GetQueueAsync")]
        [Trait("Scenario", "Make sure GetQueueAsync returns the full queue snapshot with path mapped")]
        public async Task GetQueueAsync()
        {
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithExternalId("1")
                .Build());

            var downloadClientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);

            var items = await downloadClientGateway.GetQueueAsync(client);
            Assert.Equal(2, items.Count);
            Assert.True(downloadClientAdapterMock.LastQueueRequestWasFullSnapshot);
            Assert.Null(downloadClientAdapterMock.LastRequestedQueueIds);
            Assert.Contains(items, item => item.Id == "1");
            Assert.Contains(items, item => item.Id == "2");

            foreach (QueueItem item in items)
            {
                await IsValid(item);
            }
        }

        [Fact]
        [Trait("Method", "TestConnectionAsync")]
        [Trait("Scenario", "Check that the selected mock is the right one and also TestConnectionAsync")]
        public async Task TestConnectionAsync()
        {
            var (success, message) = await downloadClientGateway.TestConnectionAsync(client);
            Assert.True(success);
            Assert.Equal("mock", message);
        }

        [Fact]
        [Trait("Method", "FetchDownloadsAsync")]
        [Trait("Scenario", "Check FetchDownloadsAsync requests only tracked IDs and path maps the matching download")]
        public async Task FetchDownloadsAsync()
        {
            var newDownload = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithExternalId("1")
                .Build());
            var downloadClientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);

            var downloads = await downloadClientGateway.FetchDownloadsAsync(client, [newDownload]);
            Assert.NotEmpty(downloads);
            Assert.Single(downloads);
            Assert.False(downloadClientAdapterMock.LastQueueRequestWasFullSnapshot);
            Assert.Equal(["1"], downloadClientAdapterMock.LastRequestedQueueIds);

            var download = downloads.First();
            Assert.NotNull(download);
            Assert.StartsWith(localPath, download.DownloadPath);
        }

        [Fact]
        [Trait("Method", "FetchDownloadsAsync")]
        [Trait("Scenario", "Check returned queue item IDs match tracked external IDs case-insensitively")]
        public async Task FetchDownloadsAsync_MatchesReturnedQueueItemIdsCaseInsensitively()
        {
            var newDownload = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithExternalId("ABC123")
                .Build());
            var downloadClientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            var path = FileUtils.GetAbsolutePath(DownloadCLientAdapterMock.RemotePath, "case-insensitive-id");
            downloadClientAdapterMock.QueueItemsMock =
            [
                new QueueItemBuilder()
                    .WithId("abc123")
                    .WithRemotePath(path)
                    .WithContentPath(path)
                    .WithSourceFile(Path.Join(path, "chapter1.mp3"))
                    .WithStatus("downloading")
                    .Build()
            ];

            var downloads = await downloadClientGateway.FetchDownloadsAsync(client, [newDownload]);

            Assert.Single(downloads);
            Assert.Equal(["ABC123"], downloadClientAdapterMock.LastRequestedQueueIds);
            Assert.StartsWith(localPath, downloads[0].DownloadPath);
        }

        [Fact]
        [Trait("Method", "FetchDownloadsAsync")]
        [Trait("Scenario", "Check monitor polling exceptions are propagated for caller-owned backoff")]
        public async Task FetchDownloadsAsync_PropagatesPollingException()
        {
            var newDownload = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithExternalId("1")
                .Build());
            var downloadClientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            var expected = new DownloadClientAdapterPollingException("client unavailable");
            downloadClientAdapterMock.FilteredQueueException = expected;

            var actual = await Assert.ThrowsAsync<DownloadClientAdapterPollingException>(
                () => downloadClientGateway.FetchDownloadsAsync(client, [newDownload]));

            Assert.Same(expected, actual);
            Assert.False(downloadClientAdapterMock.LastQueueRequestWasFullSnapshot);
            Assert.Equal(["1"], downloadClientAdapterMock.LastRequestedQueueIds);
        }

        [Fact]
        [Trait("Method", "FetchDownloadsAsync")]
        [Trait("Scenario", "Check unexpected adapter exceptions are normalized for monitor backoff")]
        public async Task FetchDownloadsAsync_WrapsUnexpectedAdapterException()
        {
            var newDownload = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithExternalId("1")
                .Build());
            var downloadClientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            var expectedInner = new InvalidOperationException("unexpected adapter failure");
            downloadClientAdapterMock.FilteredQueueException = expectedInner;

            var actual = await Assert.ThrowsAsync<DownloadClientAdapterPollingException>(
                () => downloadClientGateway.FetchDownloadsAsync(client, [newDownload]));

            Assert.Same(expectedInner, actual.InnerException);
            Assert.False(downloadClientAdapterMock.LastQueueRequestWasFullSnapshot);
            Assert.Equal(["1"], downloadClientAdapterMock.LastRequestedQueueIds);
        }

        [LinuxFact]
        public async Task GetQueueItemAsync_AmbiguousDoubleSlashPath_IsRejectedAsForeignSyntax()
        {
            var ambiguousPath = "//server/share/audiobooks/Book";
            var adapter = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway)
                .ResolveAdapter(client);
            adapter.QueueItemMock = new QueueItemBuilder()
                .WithRemotePath(ambiguousPath)
                .WithContentPath(ambiguousPath)
                .WithStatus("completed")
                .Build();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                downloadClientGateway.GetQueueItemAsync(
                    client,
                    new DownloadBuilder().Build(),
                    new QueueItem()));

            Assert.Contains("remote path mappings", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Check SourceFiles is empty when adapter gives null for both source files and content path")]
        public async Task GetQueueItemAsync_EmptyResults()
        {
            var downloadCLientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            downloadCLientAdapterMock.QueueItemMock = new QueueItemBuilder()
                .Build();

            var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());

            Assert.NotNull(item);
            Assert.NotNull(item.SourceFiles);
            Assert.Empty(item.SourceFiles);
        }

        [Fact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Check empty ContentPath is treated as missing instead of scanned")]
        public async Task GetQueueItemAsync_EmptyContentPath_DoesNotScanFilesystem()
        {
            var downloadCLientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            downloadCLientAdapterMock.QueueItemMock = new QueueItemBuilder()
                .WithContentPath(string.Empty)
                .WithStatus("downloading")
                .Build();

            var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());

            Assert.NotNull(item);
            Assert.NotNull(item.SourceFiles);
            Assert.Empty(item.SourceFiles);
        }

        [Fact]
        public void TranslateQueueItemPaths_FailedItemWithoutContentPath_DoesNotLogMissingSourceWarning()
        {
            Assert.False(DownloadClientGateway.IsImportSourceExpectedStatus("failed"));
        }

        [Fact]
        public void TranslateQueueItemPaths_CompletedItemWithoutContentPath_LogsMissingSourceWarning()
        {
            Assert.True(DownloadClientGateway.IsImportSourceExpectedStatus("completed"));
        }

        [Fact]
        public void TranslateQueueItemPaths_DownloadingItemWithoutContentPath_DoesNotLogMissingSourceWarning()
        {
            Assert.False(DownloadClientGateway.IsImportSourceExpectedStatus("downloading"));
        }

        [Fact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Check SourceFiles is filled using content path file")]
        public async Task GetQueueItemAsync_UseContentPath_File()
        {
            var sourceDirectory = FileService.GetTempDirectory("source");
            var file = await FileService.GetFileAsync(sourceDirectory, "file1.mp3");

            var downloadCLientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            downloadCLientAdapterMock.QueueItemMock = new QueueItemBuilder()
                .WithContentPath(file)
                .Build();

            var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());

            Assert.NotNull(item);
            Assert.NotNull(item.SourceFiles);
            Assert.Single(item.SourceFiles);
            Assert.Contains(file, item.SourceFiles);
        }

        [Fact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Check SourceFiles is filled using content path directory")]
        public async Task GetQueueItemAsync_UseContentPath_Directory()
        {
            var sourceDirectory = FileService.GetTempDirectory("source");
            var file1 = await FileService.GetFileAsync(sourceDirectory, "file1.mp3");
            var file2 = await FileService.GetFileAsync(sourceDirectory, "file2.mp3");

            var downloadCLientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            downloadCLientAdapterMock.QueueItemMock = new QueueItemBuilder()
                .WithContentPath(sourceDirectory)
                .Build();

            var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());

            Assert.NotNull(item);
            Assert.NotNull(item.SourceFiles);
            Assert.Equal(2, item.SourceFiles.Count);
            Assert.Contains(file1, item.SourceFiles);
            Assert.Contains(file2, item.SourceFiles);
        }

        [Fact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Check SourceFiles is empty with empty directory")]
        public async Task GetQueueItemAsync_UseContentPath_Directory_Empty()
        {
            var sourceDirectory = FileService.GetTempDirectory("source");

            var downloadCLientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            downloadCLientAdapterMock.QueueItemMock = new QueueItemBuilder()
                .WithContentPath(sourceDirectory)
                .Build();

            var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());

            Assert.NotNull(item);
            Assert.NotNull(item.SourceFiles);
            Assert.Empty(item.SourceFiles);
        }

        [Fact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Remote path mapping preserves whitespace-bearing path segments")]
        public async Task GetQueueItemAsync_PreservesWhitespaceAfterRemotePathMapping()
        {
            var remoteFile = FileUtils.GetAbsolutePath("downloads", " Book Folder ", "chapter1.m4b");
            var expectedLocalFile = Path.Join(localMapping, " Book Folder ", "chapter1.m4b");
            var downloadClientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            downloadClientAdapterMock.QueueItemMock = new QueueItemBuilder()
                .WithRemotePath(remoteFile)
                .WithContentPath(remoteFile)
                .WithSourceFile(remoteFile)
                .WithStatus("completed")
                .Build();

            var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());

            Assert.Equal(expectedLocalFile, item.LocalPath);
            Assert.Equal(expectedLocalFile, item.ContentPath);
            Assert.Equal([expectedLocalFile], item.SourceFiles);
        }

        [Fact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Exact duplicate source files are deduped")]
        public async Task GetQueueItemAsync_DedupesExactDuplicateSourceFiles()
        {
            var sourceFile = Path.Join(localPath, "chapter1.m4b");
            var downloadClientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
            downloadClientAdapterMock.QueueItemMock = new QueueItemBuilder()
                .WithSourceFile(sourceFile)
                .WithSourceFile(sourceFile)
                .WithStatus("completed")
                .Build();

            var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());

            var actual = Assert.Single(item.SourceFiles);
            Assert.Equal(sourceFile, actual);
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivity.Sensitive, 2)]
        [InlineData(FileSystemCaseSensitivity.Insensitive, 1)]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Case-only source file dedupe follows resolved storage semantics")]
        public async Task GetQueueItemAsync_DedupesCaseOnlySourceFilesUsingResolvedSemantics(
            FileSystemCaseSensitivity caseSensitivity,
            int expectedCount)
        {
            var sourceRoot = Path.Join(Path.GetTempPath(), "listenarr-gateway-semantics");
            var firstSourceFile = Path.Join(sourceRoot, "chapter1.m4b");
            var secondSourceFile = Path.Join(sourceRoot, "Chapter1.m4b");
            var item = new QueueItemBuilder()
                .WithContentPath(sourceRoot)
                .WithSourceFile(firstSourceFile)
                .WithSourceFile(secondSourceFile)
                .WithStatus("completed")
                .Build();
            var adapter = new Mock<IDownloadClientAdapter>();
            adapter.Setup(service => service.GetImportItemAsync(
                    It.IsAny<DownloadClientConfiguration>(),
                    It.IsAny<Download>(),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(item);
            var factory = new Mock<IDownloadClientAdapterFactory>();
            factory.Setup(service => service.GetByType("mock"))
                .Returns(adapter.Object);
            var mapping = new Mock<IRemotePathMappingService>();
            mapping.Setup(service => service.TranslatePathAsync(
                    It.IsAny<DownloadClientConfiguration>(),
                    It.IsAny<string>()))
                .ReturnsAsync((DownloadClientConfiguration _, string path) => path);
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(service => service.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(FileSystemPathSemantics.CurrentHostDefault.Syntax, caseSensitivity),
                        PathIdentityState.Valid,
                        path)));
            var gateway = new DownloadClientGateway(
                mapping.Object,
                factory.Object,
                _provider.GetRequiredService<IFileSystem>(),
                resolver.Object,
                _provider.GetRequiredService<ILogger<DownloadClientGateway>>());

            var result = await gateway.GetQueueItemAsync(
                new DownloadClientConfiguration { Type = "mock", Name = "mock" },
                new DownloadBuilder().Build(),
                new QueueItem());

            Assert.Equal(expectedCount, result.SourceFiles.Count);
        }

        [LinuxFact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Directory expansion preserves whitespace-bearing filesystem paths")]
        public async Task GetQueueItemAsync_ExpandsWhitespaceBearingDirectoryIntoExactSourceFiles()
        {

            var root = Path.Join(Path.GetTempPath(), "listenarr-gateway-whitespace-" + Guid.NewGuid().ToString("N"));
            var sourceDirectory = Path.Join(root, " Book Folder ");
            Directory.CreateDirectory(sourceDirectory);
            var sourceFile = Path.Join(sourceDirectory, "chapter1.m4b");
            await File.WriteAllTextAsync(sourceFile, "audio");

            try
            {
                var downloadClientAdapterMock = (DownloadCLientAdapterMock)((DownloadClientGateway)downloadClientGateway).ResolveAdapter(client);
                downloadClientAdapterMock.QueueItemMock = new QueueItemBuilder()
                    .WithContentPath(sourceDirectory)
                    .WithStatus("completed")
                    .Build();

                var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());
                var actual = Assert.Single(item.SourceFiles);

                Assert.Equal(FileUtils.NormalizeStoredPath(sourceFile), actual);
                Assert.True(File.Exists(actual));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); } catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }
    }
}
