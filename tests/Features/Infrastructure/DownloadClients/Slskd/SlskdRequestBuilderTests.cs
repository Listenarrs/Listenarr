using System.Text.Json;
using Listenarr.Infrastructure.DownloadClients.Slskd;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Slskd;

public class SlskdRequestBuilderTests
{
    [Fact]
    public void GetListenarrVisibleSourceRoot_UsesConfiguredAbsoluteRoot()
    {
        var root = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "mnt", "slskd-complete");
        var client = new DownloadClientConfiguration
        {
            Settings = new Dictionary<string, object> { ["listenarrSourceRoot"] = root }
        };

        Assert.Equal(Path.GetFullPath(root), SlskdRequestBuilder.GetListenarrVisibleSourceRoot(client));
    }

    [Fact]
    public void GetListenarrVisibleSourceRoot_RejectsRelativeRoot()
    {
        var client = new DownloadClientConfiguration
        {
            Settings = new Dictionary<string, object> { ["listenarrSourceRoot"] = "relative/downloads" }
        };

        Assert.Throws<ArgumentException>(() => SlskdRequestBuilder.GetListenarrVisibleSourceRoot(client));
    }

    [Theory]
    [InlineData("42", "listenarr/42")]
    [InlineData("book_01", "listenarr/book_01")]
    public void BuildDestination_UsesOnlyASafeListenarrRelativeId(string audiobookId, string expected)
    {
        Assert.Equal(expected, SlskdRequestBuilder.BuildDestination(audiobookId));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("book/one")]
    [InlineData("book\\one")]
    [InlineData("")]
    public void BuildDestination_RejectsUnsafeIds(string audiobookId)
    {
        Assert.Throws<ArgumentException>(() => SlskdRequestBuilder.BuildDestination(audiobookId));
    }

    [Theory]
    [InlineData("Chapter 01.m4b", 123L, true)]
    [InlineData("Part 01/Chapter.m4b", 123L, true)]
    [InlineData("book.exe", 123L, false)]
    [InlineData("empty.mp3", 0L, false)]
    public void IsSafeAudioFile_RequiresFlatAudioFilenameAndPositiveSize(string fileName, long size, bool expected)
    {
        Assert.Equal(expected, SlskdRequestBuilder.IsSafeAudioFile(fileName, size));
    }

    [Fact]
    public void MapCompletedFiles_OnlyMapsSafeAudioUnderConfiguredRoot()
    {
        var paths = SlskdRequestBuilder.MapCompletedFiles(
            "/slskd-downloads",
            "listenarr/42",
            [
                new SlskdRemoteFile("The Book/Chapter 01.m4b", 10),
                new SlskdRemoteFile("../../outside.mp3", 10),
                new SlskdRemoteFile("notes.txt", 10)
            ]);

        Assert.Equal([Path.Combine("/slskd-downloads", "listenarr", "42", "Chapter 01.m4b")], paths);
    }

    [Fact]
    public async Task SearchSubmitAndPollAsync_UsesNativeSlskdEndpointsAndExactServerFile()
    {
        var handler = new RecordingHandler(
            "{\"id\":\"search-1\"}",
            "[{\"username\":\"alice\",\"files\":[{\"filename\":\"Book/Chapter.m4b\",\"size\":12},{\"filename\":\"Book/evil.exe\",\"size\":13}]}]",
            "{\"batch\":{\"id\":\"batch-1\",\"transfers\":[]}}",
            "{\"id\":\"batch-1\",\"transfers\":[{\"filename\":\"Book/Chapter.m4b\",\"size\":12,\"state\":\"Completed, Succeeded\"}]}"
        );
        var service = CreateService(handler);
        var client = new DownloadClientConfiguration
        {
            Type = "slskd",
            Host = "slskd.internal",
            Port = 5030,
            Settings = new Dictionary<string, object>
            {
                ["apiKey"] = "test-key",
                ["listenarrSourceRoot"] = "/slskd-downloads"
            }
        };

        var result = await service.SearchSubmitAndPollAsync(client, new SlskdSubmissionRequest(42, "Author Book"));

        Assert.Equal("batch-1", result.BatchId);
        Assert.StartsWith("listenarr/42-", result.Destination, StringComparison.Ordinal);
        Assert.Equal(
            [Path.Combine("/slskd-downloads", result.Destination.Replace('/', Path.DirectorySeparatorChar), "Chapter.m4b")],
            result.CompletedFiles);
        Assert.Equal(
            ["POST /api/v0/searches", "GET /api/v0/searches/search-1/responses", "POST /api/v0/transfers/downloads/batches", "GET /api/v0/transfers/downloads/batches/batch-1"],
            handler.Calls);
        Assert.All(handler.ApiKeys, key => Assert.Equal("test-key", key));
        Assert.Contains("\"filename\":\"Book/Chapter.m4b\"", handler.RequestBodies[2]);
        Assert.DoesNotContain("evil.exe", handler.RequestBodies[2]);
    }

    [Fact]
    public async Task SearchSubmitAndPollAsync_ReservesAudiobookBeforeCallingSlskd()
    {
        var handler = new RecordingHandler(
            "{\"id\":\"search-1\"}",
            "[{\"username\":\"alice\",\"files\":[{\"filename\":\"Book/Chapter.m4b\",\"size\":12}]}]",
            "{\"batch\":{\"id\":\"batch-1\",\"transfers\":[]}}",
            "{\"id\":\"batch-1\",\"transfers\":[]}"
        );
        var repository = new Moq.Mock<IDownloadRepository>();
        repository.Setup(repo => repo.AddAsync(Moq.It.IsAny<Download>()))
            .ThrowsAsync(new UniqueConstraintViolationException("duplicate", new InvalidOperationException()));
        var service = CreateService(handler, repository: repository);

        await Assert.ThrowsAsync<DuplicateDownloadSubmissionException>(() =>
            service.SearchSubmitAndPollAsync(CreateSlskdClient(), new SlskdSubmissionRequest(42, "Author Book")));

        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(DownloadStatus.Moved)]
    [InlineData(DownloadStatus.Ready)]
    [InlineData(DownloadStatus.Completed)]
    public async Task SearchSubmitAndPollAsync_RejectsAlreadyImportedOrCompletedAudiobookBeforeCallingSlskd(DownloadStatus status)
    {
        var handler = new RecordingHandler();
        var repository = new Moq.Mock<IDownloadRepository>();
        repository.Setup(repo => repo.GetByAudiobookIdAsync(42, Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Download { AudiobookId = 42, Status = status, LastImportedAt = status == DownloadStatus.Moved ? DateTime.UtcNow : null }]);
        var service = CreateService(handler, repository: repository);

        var exception = await Assert.ThrowsAsync<DuplicateDownloadSubmissionException>(() =>
            service.SearchSubmitAndPollAsync(CreateSlskdClient(), new SlskdSubmissionRequest(42, "Author Book")));

        Assert.Contains("already active or imported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Calls);
        repository.Verify(repo => repo.AddAsync(Moq.It.IsAny<Download>()), Moq.Times.Never);
    }

    [Fact]
    public async Task SearchSubmitAndPollAsync_SubmitsOneDeduplicatedBatchThatKeepsDistinctChapters()
    {
        var handler = new RecordingHandler(
            "{\"id\":\"search-1\"}",
            "[{\"username\":\"alice\",\"files\":[{\"filename\":\"Book/Chapter 01.m4b\",\"size\":12},{\"filename\":\"Book/Chapter 01.m4b\",\"size\":12},{\"filename\":\"Book/Chapter 02.m4b\",\"size\":13}]}]",
            "{\"batch\":{\"id\":\"batch-1\",\"transfers\":[]}}",
            "{\"id\":\"batch-1\",\"transfers\":[]}"
        );

        await CreateService(handler).SearchSubmitAndPollAsync(
            CreateSlskdClient(),
            new SlskdSubmissionRequest(42, "Author Book"));

        Assert.Single(handler.Calls, call => call == "POST /api/v0/transfers/downloads/batches");
        using var request = JsonDocument.Parse(handler.RequestBodies[2]);
        var files = request.RootElement.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(["Book/Chapter 01.m4b", "Book/Chapter 02.m4b"],
            files.Select(file => file.GetProperty("filename").GetString()).ToList());
    }

    [Fact]
    public async Task SearchSubmitAndPollAsync_UsesAnIsolatedStagingDestinationPerReservation()
    {
        var handler = new RecordingHandler(
            "{\"id\":\"search-1\"}",
            "[{\"username\":\"alice\",\"files\":[{\"filename\":\"Book/Chapter.m4b\",\"size\":12}]}]",
            "{\"batch\":{\"id\":\"batch-1\",\"transfers\":[]}}",
            "{\"id\":\"batch-1\",\"transfers\":[]}"
        );

        await CreateService(handler).SearchSubmitAndPollAsync(
            CreateSlskdClient(),
            new SlskdSubmissionRequest(42, "Author Book"));

        using var request = JsonDocument.Parse(handler.RequestBodies[2]);
        var destination = request.RootElement.GetProperty("options").GetProperty("destination").GetString();
        Assert.StartsWith("listenarr/42-", destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchSubmitAndPollAsync_PollsUntilASafeAudioResponseIsAvailable()
    {
        var handler = new RecordingHandler(
            "{\"id\":\"search-1\"}",
            "[]",
            "[{\"username\":\"alice\",\"files\":[{\"filename\":\"Book/Chapter.m4b\",\"size\":12}]}]",
            "{\"batch\":{\"id\":\"batch-1\",\"transfers\":[]}}",
            "{\"id\":\"batch-1\",\"transfers\":[]}"
        );
        var service = CreateService(handler);

        var result = await service.SearchSubmitAndPollAsync(CreateSlskdClient(), new SlskdSubmissionRequest(42, "Author Book"));

        Assert.Equal("batch-1", result.BatchId);
        Assert.Equal(
            ["POST /api/v0/searches", "GET /api/v0/searches/search-1/responses", "GET /api/v0/searches/search-1/responses", "POST /api/v0/transfers/downloads/batches", "GET /api/v0/transfers/downloads/batches/batch-1"],
            handler.Calls);
    }

    [Fact]
    public async Task SearchSubmitAndPollAsync_PrefersSafeCandidateWithFreeUploadSlot()
    {
        var handler = new RecordingHandler(
            "{\"id\":\"search-1\"}",
            "[{\"username\":\"queued\",\"hasFreeUploadSlot\":false,\"queueLength\":0,\"files\":[{\"filename\":\"Book/One.m4b\",\"size\":12}]},{\"username\":\"ready\",\"hasFreeUploadSlot\":true,\"queueLength\":5,\"files\":[{\"filename\":\"Book/One.m4b\",\"size\":12}]}]",
            "{\"batch\":{\"id\":\"batch-1\",\"transfers\":[]}}",
            "{\"id\":\"batch-1\",\"transfers\":[]}"
        );

        await CreateService(handler).SearchSubmitAndPollAsync(CreateSlskdClient(), new SlskdSubmissionRequest(42, "Author Book"));

        using var request = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("ready", request.RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public async Task SearchSubmitAndPollAsync_RetriesRateLimitedResponsePollingWithinTheBoundedSearch()
    {
        var handler = new RecordingHandler(
            "{\"id\":\"search-1\"}",
            (System.Net.HttpStatusCode.TooManyRequests, "", TimeSpan.FromMilliseconds(1)),
            "[{\"username\":\"alice\",\"files\":[{\"filename\":\"Book/Chapter.m4b\",\"size\":12}]}]",
            "{\"batch\":{\"id\":\"batch-1\",\"transfers\":[]}}",
            "{\"id\":\"batch-1\",\"transfers\":[]}"
        );
        var service = CreateService(handler);

        var result = await service.SearchSubmitAndPollAsync(CreateSlskdClient(), new SlskdSubmissionRequest(42, "Author Book"));

        Assert.Equal("batch-1", result.BatchId);
        Assert.Equal(2, handler.Calls.Count(call => call == "GET /api/v0/searches/search-1/responses"));
    }

    [Fact]
    public async Task SearchSubmitAndPollAsync_TimesOutWithActionableErrorWhenNoSafeAudioResponseArrives()
    {
        var service = CreateService(new SearchWithoutResultsHandler(), new SlskdSearchPollingOptions(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(1)));

        var exception = await Assert.ThrowsAsync<DownloadClientSubmissionException>(() =>
            service.SearchSubmitAndPollAsync(CreateSlskdClient(), new SlskdSubmissionRequest(42, "Author Book")));

        Assert.Contains("did not return a safe audio result", exception.Message);
        Assert.Contains("Verify the query or try again", exception.Message);
    }

    [Fact]
    public async Task SearchSubmitAndPollAsync_PreservesCallerCancellationWhilePolling()
    {
        var service = CreateService(new SearchWithoutResultsHandler(), new SlskdSearchPollingOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchSubmitAndPollAsync(CreateSlskdClient(), new SlskdSubmissionRequest(42, "Author Book"), cancellation.Token));
    }

    private static DownloadClientConfiguration CreateSlskdClient() => new()
    {
        Type = "slskd",
        Host = "slskd.internal",
        Port = 5030,
        Settings = new Dictionary<string, object>
        {
            ["apiKey"] = "test-key",
            ["listenarrSourceRoot"] = "/slskd-downloads"
        }
    };

    private static SlskdDownloadService CreateService(
        HttpMessageHandler handler,
        SlskdSearchPollingOptions? polling = null,
        Moq.Mock<IDownloadRepository>? repository = null)
    {
        if (repository is null)
        {
            repository = new Moq.Mock<IDownloadRepository>();
            repository.Setup(repo => repo.AddAsync(Moq.It.IsAny<Download>())).ReturnsAsync((Download download) => download);
        }
        return new SlskdDownloadService(
            new SingleClientFactory(new HttpClient(handler)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SlskdDownloadService>.Instance,
            repository.Object,
            polling ?? new SlskdSearchPollingOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1)));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(params object[] responses) : HttpMessageHandler
    {
        private readonly Queue<object> _responses = new(responses);
        public List<string> Calls { get; } = [];
        public List<string> ApiKeys { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            ApiKeys.Add(request.Headers.TryGetValues("X-API-Key", out var apiKeys) ? apiKeys.Single() : string.Empty);
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var next = _responses.Dequeue();
            var (statusCode, body, retryAfter) = next switch
            {
                string response => (System.Net.HttpStatusCode.OK, response, (TimeSpan?)null),
                ValueTuple<System.Net.HttpStatusCode, string> response => (response.Item1, response.Item2, null),
                ValueTuple<System.Net.HttpStatusCode, string, TimeSpan> response => (response.Item1, response.Item2, (TimeSpan?)response.Item3),
                _ => throw new InvalidOperationException("Unexpected test response.")
            };
            var message = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            if (retryAfter is not null)
                message.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
            return message;
        }
    }

    private sealed class SearchWithoutResultsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v0/searches" ? "{\"id\":\"search-1\"}" : "[]",
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
    }

    [Fact]
    public async Task BuildBatchRequest_UsesExactValidatedRemoteFileAndControlledDestination()
    {
        using var request = SlskdRequestBuilder.BuildBatchRequest(
            "42",
            "alice",
            new[] { new SlskdRemoteFile("The Book/Chapter 01.m4b", 123456) });

        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal("alice", json.RootElement.GetProperty("username").GetString());
        Assert.Equal("listenarr/42", json.RootElement.GetProperty("options").GetProperty("destination").GetString());
        var file = Assert.Single(json.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal("The Book/Chapter 01.m4b", file.GetProperty("filename").GetString());
        Assert.Equal(123456, file.GetProperty("size").GetInt64());
        Assert.Equal("/api/v0/transfers/downloads/batches", request.RequestUri!.OriginalString);
        Assert.Equal(System.Net.Http.HttpMethod.Post, request.Method);
    }
}
