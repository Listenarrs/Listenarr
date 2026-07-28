using System.Net;
using System.Text;
using Listenarr.Infrastructure.DownloadClients.Slskd;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Slskd;

public sealed class SlskdAdapterTests
{
    [Fact]
    public async Task GetQueueAsync_AllSuccessfulTerminalTransfers_MapsOnlyBasenamesUnderSlskdDownloads()
    {
        var adapter = CreateAdapter("""
            {
              "id": "batch-1",
              "transfers": [
                { "filename": "Author\\Book\\Chapter 01.m4b", "size": 12, "bytesTransferred": 12, "state": "Completed, Succeeded" },
                { "filename": "Author/Book/Chapter 02.mp3", "size": 8, "bytesTransferred": 8, "state": "Completed, Succeeded" }
              ],
              "options": { "destination": "listenarr/42" }
            }
            """);

        var item = Assert.Single(await adapter.GetQueueAsync(CreateClient(), ["batch-1"]));

        Assert.Equal("batch-1", item.Id);
        Assert.Equal("completed", item.Status);
        Assert.True(item.CanRemove);
        Assert.Equal(100d, item.Progress);
        Assert.Equal(
            ["/slskd-downloads/listenarr/42/Chapter 01.m4b", "/slskd-downloads/listenarr/42/Chapter 02.mp3"],
            item.SourceFiles);
        Assert.All(item.SourceFiles!, path => Assert.StartsWith("/slskd-downloads/", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetQueueAsync_FullSnapshot_ResolvesNativeBatchIdsForOrphanSafety()
    {
        const string batchId = "11111111-1111-1111-1111-111111111111";
        var adapter = new SlskdAdapter(
            new SingleClientFactory(new HttpClient(new RouteHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/api/v0/transfers/downloads" => """[{"username":"alice","directories":[{"files":[{"batchId":"11111111-1111-1111-1111-111111111111"}]}]}]""",
                _ => """{"id":"11111111-1111-1111-1111-111111111111","transfers":[{"filename":"Chapter.m4b","size":12,"bytesTransferred":0,"state":"Queued, Remotely"}],"options":{"destination":"listenarr/42"}}"""
            }))),
            NullLogger<SlskdAdapter>.Instance,
            Moq.Mock.Of<IFileSystem>());

        var item = Assert.Single(await adapter.GetQueueAsync(CreateClient()));

        Assert.Equal(batchId, item.Id, ignoreCase: true);
        Assert.Equal("queued", item.Status);
    }

    [Fact]
    public async Task RemoveAsync_RemovesEveryTransferInTheBatchWithoutCollapsingChapters()
    {
        var calls = new List<string>();
        var adapter = new SlskdAdapter(
            new SingleClientFactory(new HttpClient(new RouteResponseHandler(request =>
            {
                calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
                return request.Method == HttpMethod.Get
                    ? (HttpStatusCode.OK, """
                        {"id":"batch-1","transfers":[
                          {"id":"11111111-1111-1111-1111-111111111111","username":"alice","filename":"Book/Chapter 01.m4b","size":12,"state":"Completed, Succeeded"},
                          {"id":"22222222-2222-2222-2222-222222222222","username":"alice","filename":"Book/Chapter 02.m4b","size":13,"state":"Completed, Succeeded"}
                        ],"options":{"destination":"listenarr/42-reservation"}}
                        """)
                    : (HttpStatusCode.NoContent, string.Empty);
            }))),
            NullLogger<SlskdAdapter>.Instance,
            Moq.Mock.Of<IFileSystem>());

        var removed = await adapter.RemoveAsync(CreateClient(), "batch-1", deleteFiles: false);

        Assert.True(removed);
        Assert.Equal(
            [
                "GET /api/v0/transfers/downloads/batches/batch-1",
                "DELETE /api/v0/transfers/downloads/alice/11111111-1111-1111-1111-111111111111?remove=true",
                "DELETE /api/v0/transfers/downloads/alice/22222222-2222-2222-2222-222222222222?remove=true"
            ],
            calls);
    }

    [Fact]
    public async Task GetQueueAsync_PartiallyQueuedBatch_DoesNotExposeImportPathsOrCompletion()
    {
        var adapter = CreateAdapter("""
            {
              "id": "batch-1",
              "transfers": [
                { "filename": "Book/Chapter 01.m4b", "size": 12, "bytesTransferred": 12, "state": "Completed, Succeeded" },
                { "filename": "Book/Chapter 02.m4b", "size": 12, "bytesTransferred": 0, "state": "Queued, Remotely" }
              ],
              "options": { "destination": "listenarr/42" }
            }
            """);

        var item = Assert.Single(await adapter.GetQueueAsync(CreateClient(), ["batch-1"]));

        Assert.NotEqual("completed", item.Status);
        Assert.Null(item.ContentPath);
        Assert.Null(item.RemotePath);
        Assert.Null(item.SourceFiles);
    }

    [Fact]
    public async Task GetQueueAsync_CollisionRenamedChapters_UsesActualFilesFromTheIsolatedStage()
    {
        var fileSystem = new Moq.Mock<IFileSystem>();
        const string stage = "/slskd-downloads/listenarr/42-reservation";
        fileSystem.Setup(fs => fs.DirectoryExists(stage)).Returns(true);
        fileSystem.Setup(fs => fs.EnumerateFiles(stage)).Returns(
            [stage + "/Chapter.m4b", stage + "/Chapter_638900000000000000.m4b"]);
        fileSystem.Setup(fs => fs.GetFileLength(Moq.It.IsAny<string>())).Returns(12);
        var adapter = new SlskdAdapter(
            new SingleClientFactory(new HttpClient(new StaticHandler("""
                {"id":"batch-1","transfers":[
                  {"filename":"Disc 1/Chapter.m4b","size":12,"bytesTransferred":12,"state":"Completed, Succeeded"},
                  {"filename":"Disc 2/Chapter.m4b","size":12,"bytesTransferred":12,"state":"Completed, Succeeded"}
                ],"options":{"destination":"listenarr/42-reservation"}}
                """))),
            NullLogger<SlskdAdapter>.Instance,
            fileSystem.Object);

        var item = Assert.Single(await adapter.GetQueueAsync(CreateClient(), ["batch-1"]));

        Assert.Equal("completed", item.Status);
        Assert.Equal(
            [stage + "/Chapter.m4b", stage + "/Chapter_638900000000000000.m4b"],
            item.SourceFiles);
    }

    [Fact]
    public async Task RemoveAsync_DeleteFiles_RemovesAllActualFilesAndTheIsolatedStageDirectory()
    {
        const string stage = "/slskd-downloads/listenarr/42-reservation";
        var fileSystem = new Moq.Mock<IFileSystem>();
        fileSystem.Setup(fs => fs.DirectoryExists(stage)).Returns(true);
        fileSystem.Setup(fs => fs.EnumerateFiles(stage)).Returns(
            [stage + "/Chapter.m4b", stage + "/Chapter_638900000000000000.m4b"]);
        fileSystem.Setup(fs => fs.GetFileLength(Moq.It.IsAny<string>())).Returns(12);
        fileSystem.Setup(fs => fs.FileExists(Moq.It.IsAny<string>())).Returns(true);
        string normalized = string.Empty;
        string reason = string.Empty;
        fileSystem.Setup(fs => fs.TryValidateMutationTarget(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<IEnumerable<string?>>(),
                out normalized,
                out reason))
            .Returns(true);
        var adapter = new SlskdAdapter(
            new SingleClientFactory(new HttpClient(new RouteResponseHandler(request =>
                request.Method == HttpMethod.Get
                    ? (HttpStatusCode.OK, """
                        {"id":"batch-1","transfers":[
                          {"id":"11111111-1111-1111-1111-111111111111","username":"alice","filename":"Disc 1/Chapter.m4b","size":12,"state":"Completed, Succeeded"},
                          {"id":"22222222-2222-2222-2222-222222222222","username":"alice","filename":"Disc 2/Chapter.m4b","size":12,"state":"Completed, Succeeded"}
                        ],"options":{"destination":"listenarr/42-reservation"}}
                        """)
                    : (HttpStatusCode.NoContent, string.Empty)))),
            NullLogger<SlskdAdapter>.Instance,
            fileSystem.Object);

        Assert.True(await adapter.RemoveAsync(CreateClient(), "batch-1", deleteFiles: true));
        fileSystem.Verify(fs => fs.DeleteFile(stage + "/Chapter.m4b"), Moq.Times.Once);
        fileSystem.Verify(fs => fs.DeleteFile(stage + "/Chapter_638900000000000000.m4b"), Moq.Times.Once);
        fileSystem.Verify(fs => fs.DeleteEmptyDirectories(stage), Moq.Times.Once);
    }

    [Fact]
    public async Task MarkItemAsImportedAsync_PartialBatchLeavesStageAndTransfersUntouched()
    {
        const string stage = "/slskd-downloads/listenarr/42-reservation";
        var fileSystem = new Moq.Mock<IFileSystem>();
        fileSystem.Setup(fs => fs.DirectoryExists(stage)).Returns(true);
        fileSystem.Setup(fs => fs.EnumerateFiles(stage)).Returns([stage + "/Chapter 01.m4b", stage + "/Chapter 02.m4b"]);
        var calls = new List<HttpMethod>();
        var adapter = new SlskdAdapter(
            new SingleClientFactory(new HttpClient(new RouteResponseHandler(request =>
            {
                calls.Add(request.Method);
                return (HttpStatusCode.OK, """
                    {"id":"batch-1","transfers":[
                      {"id":"11111111-1111-1111-1111-111111111111","username":"alice","filename":"Chapter 01.m4b","size":12,"state":"Completed, Succeeded"},
                      {"id":"22222222-2222-2222-2222-222222222222","username":"alice","filename":"Chapter 02.m4b","size":12,"state":"Queued, Remotely"}
                    ],"options":{"destination":"listenarr/42-reservation"}}
                    """);
            }))),
            NullLogger<SlskdAdapter>.Instance,
            fileSystem.Object);

        Assert.False(await adapter.MarkItemAsImportedAsync(CreateClient(), "batch-1"));
        Assert.Equal([HttpMethod.Get], calls);
        fileSystem.Verify(fs => fs.DeleteFile(Moq.It.IsAny<string>()), Moq.Times.Never);
        fileSystem.Verify(fs => fs.DeleteEmptyDirectories(Moq.It.IsAny<string>()), Moq.Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkItemAsImportedAsync_AfterFilesWereMoved_RemovesSuccessfulTransfers(bool stageDirectoryExists)
    {
        const string stage = "/slskd-downloads/listenarr/42-reservation";
        var fileSystem = new Moq.Mock<IFileSystem>();
        fileSystem.Setup(fs => fs.DirectoryExists(stage)).Returns(stageDirectoryExists);
        fileSystem.Setup(fs => fs.EnumerateFiles(stage)).Returns([]);
        var calls = new List<HttpMethod>();
        var adapter = new SlskdAdapter(
            new SingleClientFactory(new HttpClient(new RouteResponseHandler(request =>
            {
                calls.Add(request.Method);
                return request.Method == HttpMethod.Get
                    ? (HttpStatusCode.OK, """
                        {"id":"batch-1","transfers":[
                          {"id":"11111111-1111-1111-1111-111111111111","username":"alice","filename":"Chapter.m4b","size":12,"state":"Completed, Succeeded"}
                        ],"options":{"destination":"listenarr/42-reservation"}}
                        """)
                    : (HttpStatusCode.NoContent, string.Empty);
            }))),
            NullLogger<SlskdAdapter>.Instance,
            fileSystem.Object);

        Assert.True(await adapter.MarkItemAsImportedAsync(CreateClient(), "batch-1"));
        Assert.Equal([HttpMethod.Get, HttpMethod.Delete], calls);
        fileSystem.Verify(fs => fs.DeleteFile(Moq.It.IsAny<string>()), Moq.Times.Never);
        fileSystem.Verify(fs => fs.DeleteEmptyDirectories(stage),
            stageDirectoryExists ? Moq.Times.Once() : Moq.Times.Never());
    }

    [Fact]
    public async Task MarkItemAsImportedAsync_UnverifiedNonEmptyStageLeavesTransfersUntouched()
    {
        const string stage = "/slskd-downloads/listenarr/42-reservation";
        var fileSystem = new Moq.Mock<IFileSystem>();
        fileSystem.Setup(fs => fs.DirectoryExists(stage)).Returns(true);
        fileSystem.Setup(fs => fs.EnumerateFiles(stage)).Returns([stage + "/unexpected.txt"]);
        fileSystem.Setup(fs => fs.GetFileLength(stage + "/unexpected.txt")).Returns(99);
        var calls = new List<HttpMethod>();
        var adapter = new SlskdAdapter(
            new SingleClientFactory(new HttpClient(new RouteResponseHandler(request =>
            {
                calls.Add(request.Method);
                return (HttpStatusCode.OK, """
                    {"id":"batch-1","transfers":[
                      {"id":"11111111-1111-1111-1111-111111111111","username":"alice","filename":"Chapter.m4b","size":12,"state":"Completed, Succeeded"}
                    ],"options":{"destination":"listenarr/42-reservation"}}
                    """);
            }))),
            NullLogger<SlskdAdapter>.Instance,
            fileSystem.Object);

        Assert.False(await adapter.MarkItemAsImportedAsync(CreateClient(), "batch-1"));
        Assert.Equal([HttpMethod.Get], calls);
        fileSystem.Verify(fs => fs.DeleteFile(Moq.It.IsAny<string>()), Moq.Times.Never);
    }

    [Fact]
    public async Task GetQueueAsync_UnexpectedDestination_DoesNotMarkTerminalBatchComplete()
    {
        var adapter = CreateAdapter("""
            {
              "id": "batch-1",
              "transfers": [
                { "filename": "Chapter 01.m4b", "size": 12, "bytesTransferred": 12, "state": "Completed, Succeeded" }
              ],
              "options": { "destination": "../../outside" }
            }
            """);

        var item = Assert.Single(await adapter.GetQueueAsync(CreateClient(), ["batch-1"]));

        Assert.NotEqual("completed", item.Status);
        Assert.Null(item.SourceFiles);
        Assert.Null(item.ContentPath);
    }

    private static SlskdAdapter CreateAdapter(string response)
    {
        const string stage = "/slskd-downloads/listenarr/42";
        var fileSystem = new Moq.Mock<IFileSystem>();
        fileSystem.Setup(fs => fs.DirectoryExists(stage)).Returns(true);
        fileSystem.Setup(fs => fs.EnumerateFiles(stage)).Returns([stage + "/Chapter 01.m4b", stage + "/Chapter 02.mp3"]);
        fileSystem.Setup(fs => fs.GetFileLength(stage + "/Chapter 01.m4b")).Returns(12);
        fileSystem.Setup(fs => fs.GetFileLength(stage + "/Chapter 02.mp3")).Returns(8);
        return new SlskdAdapter(
            new SingleClientFactory(new HttpClient(new StaticHandler(response))),
            NullLogger<SlskdAdapter>.Instance,
            fileSystem.Object);
    }

    private static DownloadClientConfiguration CreateClient(string sourceRoot = "/slskd-downloads") => new()
    {
        Id = "slskd-client",
        Type = "slskd",
        Host = "slskd.internal",
        Port = 5030,
        Settings = new Dictionary<string, object> { ["listenarrSourceRoot"] = sourceRoot }
    };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, string> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseFactory(request), Encoding.UTF8, "application/json")
            });
    }

    private sealed class RouteResponseHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responseFactory(request);
            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StaticHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
    }
}
