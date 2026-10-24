using System.Text.Json;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent;

[Trait("Name", nameof(QbittorrentRdtClientCompatibilityTests))]
[Trait("Category", "DownloadClients")]
public class QbittorrentRdtClientCompatibilityTests : BaseTests
{
    [Theory]
    [InlineData("0", true)]
    [InlineData("0.0", true)]
    [InlineData("-0", true)]
    [InlineData("-0.00E+12", true)]
    [InlineData("0e400", true)]
    [InlineData("0e-400", true)]
    [InlineData("1e-12", false)]
    [InlineData("5e-324", false)]
    [InlineData("2e-324", false)]
    [InlineData("1e-324", false)]
    [InlineData("1e-400", false)]
    [InlineData("-1e-400", false)]
    [InlineData("0.0001e-400", false)]
    [InlineData("1e400", false)]
    [InlineData("\"0\"", false)]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("[]", false)]
    [InlineData("{}", false)]
    public void MetadataCompatibility_RequiresExactJsonZeroInBothFields(string rawNumber, bool expected)
    {
        foreach (var key in new[] { "priority", "progress" })
        {
            var files = ParseFiles("""[{"name":"Book/a.mp3","priority":0,"progress":0}]""");
            files[0][key] = JsonSerializer.Deserialize<JsonElement>(rawNumber);

            Assert.Equal(expected, QbittorrentImportPathResolver.HasUnusableFileMetadata(files));
        }

        var item = QbittorrentResponseMapper.MapQueueItem(
            ParseObject("""{"hash":"exact","state":"pausedUP","progress":1,"save_path":"/downloads"}"""),
            new DownloadClientConfiguration(),
            ParseFiles("[{\"name\":\"Book/a.mp3\",\"priority\":0,\"progress\":" + rawNumber + "}]"),
            isRdtClient: true);

        Assert.Equal(expected ? 1 : 0, item.SourceFiles!.Count);
    }

    [Theory]
    [InlineData(false, "pausedUP", 1, 0, 0, 0)]
    [InlineData(true, "pausedUP", 1, 0, 0, 1)]
    [InlineData(true, "stoppedUP", 1, 0, 0, 1)]
    [InlineData(true, "uploading", 1, 0, 0, 1)]
    [InlineData(true, "pausedUP", 0.5, 0, 0, 0)]
    [InlineData(true, "downloading", 0.5, 0, 0, 0)]
    [InlineData(true, "checkingResumeData", 1, 0, 0, 0)]
    [InlineData(true, "moving", 1, 0, 0, 0)]
    [InlineData(true, "error", 1, 0, 0, 0)]
    [InlineData(true, "missingFiles", 1, 0, 0, 0)]
    [InlineData(true, "pausedUP", 1, 0, 1, 0)]
    [InlineData(true, "pausedUP", 1, 1, 1, 1)]
    public void QueueMapping_CompatibilityRequiresDetectionCompletionAndAllZeroMetadata(
        bool detected, string state, double progress, int priority, double fileProgress, int expectedFiles)
    {
        var client = new DownloadClientConfiguration();
        var torrent = ParseObject(JsonSerializer.Serialize(new
        {
            hash = "exact",
            state,
            progress,
            save_path = "/downloads",
            content_path = "/downloads/Book"
        }));
        var files = ParseFiles(JsonSerializer.Serialize(new[] { new { name = "Book/chapter.mp3", priority, progress = fileProgress } }));

        var item = QbittorrentResponseMapper.MapQueueItem(torrent, client, files, isRdtClient: detected);

        Assert.Equal(expectedFiles, item.SourceFiles!.Count);
        Assert.Equal(expectedFiles > 0 ? "/downloads/Book" : "", item.ContentPath);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"name\":\"Book/a.mp3\",\"priority\":0}]")]
    [InlineData("[{\"name\":\"Book/a.mp3\",\"priority\":\"0\",\"progress\":0}]")]
    [InlineData("[{\"name\":\"Book/a.mp3\",\"priority\":0,\"progress\":null}]")]
    public void QueueMapping_IncompleteOrMalformedMetadataDoesNotEnableCompatibility(string json)
    {
        var item = QbittorrentResponseMapper.MapQueueItem(
            ParseObject("""{"hash":"exact","state":"pausedUP","progress":1,"save_path":"/downloads"}"""),
            new DownloadClientConfiguration(),
            ParseFiles(json), isRdtClient: true);

        Assert.Empty(item.SourceFiles!);
        Assert.Empty(item.ContentPath!);
    }

    [Fact]
    public void QueueMapping_MixedPrioritiesStillExcludeDeselectedFiles()
    {
        var item = QbittorrentResponseMapper.MapQueueItem(
            ParseObject("""{"hash":"exact","state":"pausedUP","progress":1,"save_path":"/downloads"}"""),
            new DownloadClientConfiguration(),
            ParseFiles("""[{"name":"Book/a.mp3","priority":1,"progress":1},{"name":"Other/b.mp3","priority":0,"progress":0}]"""), isRdtClient: true);

        Assert.Equal(["/downloads/Book/a.mp3"], item.SourceFiles);
        Assert.Equal("/downloads/Book", item.ContentPath);
    }

    [Theory]
    [InlineData("../foreign.mp3")]
    [InlineData("Book/../../foreign.mp3")]
    public void QueueMapping_CompatibilityRetainsTraversalProtection(string name)
    {
        var item = QbittorrentResponseMapper.MapQueueItem(
            ParseObject("""{"hash":"exact","state":"pausedUP","progress":1,"save_path":"/downloads"}"""),
            new DownloadClientConfiguration(),
            ParseFiles(JsonSerializer.Serialize(new[] { new { name, priority = 0, progress = 0 } })), isRdtClient: true);

        Assert.Empty(item.SourceFiles!);
        Assert.Empty(item.ContentPath!);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("incomplete")]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("unavailable")]
    [InlineData("malformed")]
    [InlineData("ordinary")]
    [InlineData("forged")]
    [InlineData("native-malformed")]
    public async Task ImportResolution_RequiresFreshExactCompletionOverHttp(string scenario)
    {
        await using var server = await StartServerAsync(scenario);
        var client = CreateClient(server);
        var result = await new QbittorrentImportItemResolver(NullLogger.Instance).GetImportItemAsync(
            client, new Download { Id = "download" }, new QueueItem { Id = "EXACT", Status = "completed" });

        if (scenario == "completed")
        {
            Assert.Equal(45, result.SourceFiles!.Count);
            Assert.All(result.SourceFiles, file => Assert.True(File.Exists(file)));
        }
        else
        {
            Assert.True(result.SourceFiles == null || result.SourceFiles.Count == 0);
            Assert.True(string.IsNullOrEmpty(result.ContentPath));
        }
    }

    [Fact]
    public async Task ImportResolution_CompatibilityDoesNotEnumerateSharedDirectoryOrHideMissingFiles()
    {
        await using var server = await StartServerAsync("completed");
        var client = CreateClient(server);
        var sourceDirectory = FileService.GetTempDirectory("payload");
        var foreign = await FileService.GetFileAsync(sourceDirectory, "foreign.mp3");
        var missing = Path.Join(sourceDirectory, "Book", "01.mp3");
        File.Delete(missing);

        var result = await new QbittorrentImportItemResolver(NullLogger.Instance).GetImportItemAsync(
            client, new Download { Id = "download" }, new QueueItem { Id = "EXACT", Status = "completed" });

        Assert.Equal(45, result.SourceFiles!.Count);
        Assert.Contains(missing, result.SourceFiles);
        Assert.DoesNotContain(foreign, result.SourceFiles);
        Assert.Equal(44, result.SourceFiles.Count(File.Exists));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task ImportProcessing_OnlyImportsCompleteExplicitFileSet(bool detected, bool missingFile, bool remoteMapping)
    {
        await using var server = await StartServerAsync(detected ? "completed" : "ordinary", remoteMapping);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        Init(builder => builder
            .WithSingleton(factory.Object)
            .WithSingleton<IDownloadItemService, DownloadItemService>());
        await AddAuthorizedRootAsync(FileService.GetTempPath());

        var client = CreateClient(server);
        client.Type = "qbittorrent";
        client = await _downloadClientConfigurationRepository.SaveAsync(client);
        var sourceDirectory = FileService.GetTempDirectory("payload");
        if (remoteMapping)
        {
            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client).WithRemotePath("/remote/downloads")
                .WithLocalPath(sourceDirectory).Build());
        }
        var bookDirectory = FileService.GetTempDirectory(Path.Join("payload", "Book"));
        var destination = FileService.GetTempDirectory("library");
        var foreign = await FileService.GetFileAsync(bookDirectory, "foreign.mp3");
        if (missingFile) File.Delete(Path.Join(bookDirectory, "01.mp3"));

        await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
            .WithOutputPath(destination).WithCopyFileOnCompleted().WithoutMetadataProcessing().Build());
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithBasePath(destination).Build());
        var download = await _downloadRepository.AddAsync(new DownloadBuilder()
            .WithCompletedStatus(DateTime.UtcNow).WithPath(sourceDirectory).WithExternalId("exact")
            .WithDownloadClientConfiguration(client).WithAudiobook(audiobook).Build());
        var jobId = await _provider.GetRequiredService<IDownloadProcessingJobService>().EnqueueAsync(download);

        await _provider.GetRequiredService<DownloadProcessingJobProcessor>().ProcessQueueAsync(CancellationToken.None);

        var job = (await _downloadProcessingJobRepository.GetByIdAsync(jobId))!;
        var copied = Directory.GetFiles(destination, "*.mp3", SearchOption.AllDirectories);
        var registered = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
        if (detected && !missingFile)
        {
            Assert.True(job.Status == ProcessingJobStatus.Completed,
                $"{job.ErrorMessage}; {string.Join(" | ", job.ProcessingLog)}");
            Assert.Equal(45, copied.Length);
            Assert.Equal(45, registered.Count);
            Assert.DoesNotContain(copied, path => Path.GetFileName(path) == "foreign.mp3");
        }
        else
        {
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
            Assert.True(job.RetryCount > 0);
            Assert.NotNull(job.NextRetryAt);
            Assert.Empty(copied);
            Assert.Empty(registered);
        }
        Assert.True(File.Exists(foreign));
        Assert.Equal(missingFile ? 45 : 46, Directory.GetFiles(bookDirectory, "*.mp3").Length);
    }

    [Theory]
    [InlineData("completed", 45)]
    [InlineData("ordinary", 0)]
    [InlineData("incomplete", 0)]
    [InlineData("forged", 0)]
    [InlineData("native-malformed", 0)]
    public async Task QueuePolling_AutomaticallyDetectsRdtClientOverHttp(string scenario, int expectedFiles)
    {
        await using var server = await StartServerAsync(scenario);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        var logger = NullLogger<QbittorrentAdapter>.Instance;
        var workflow = new QbittorrentQueueFetchWorkflow(factory.Object, new QbittorrentAuthSession(logger), logger, "qbittorrent");

        var item = Assert.Single(await workflow.GetQueueAsync(CreateClient(server), ["exact"]));

        Assert.Equal(expectedFiles, item.SourceFiles!.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"created_by\":\"rdt-client\",\"comment\":\"rdt-client\"}")]
    [InlineData("{\"created_by\":null,\"comment\":false}")]
    [InlineData("{\"created_by\":\"RealDebridClient <https://github.com/rogerfar/rdt-client>\"}")]
    public void Detection_DoesNotAcceptPartialOrMalformedSignatures(string properties)
    {
        Assert.False(QbittorrentCompatibilityDetector.HasRdtClientSignature(ParseObject(properties)));
    }

    [Theory]
    [InlineData("2.0.115.99", false)]
    [InlineData("2.0.116", true)]
    [InlineData("2.0.116.0", true)]
    [InlineData("2.0.129", true)]
    [InlineData("2.0.129.999", true)]
    [InlineData("2.0.130", false)]
    [InlineData("2.0.130.0", false)]
    [InlineData("2.0.143.0", false)]
    [InlineData("2.1.0", false)]
    [InlineData("3.0.0", false)]
    [InlineData("2.0", false)]
    [InlineData("2.0.129-custom", false)]
    [InlineData("invalid", false)]
    [InlineData(null, false)]
    public async Task Recovery_UsesAuditedLegacyVersionsInBothPollingAndImport(string? version, bool eligible)
    {
        await using var server = await StartServerAsync("completed", nativeVersion: version);
        var client = CreateClient(server);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        var logger = NullLogger<QbittorrentAdapter>.Instance;
        var workflow = new QbittorrentQueueFetchWorkflow(factory.Object, new QbittorrentAuthSession(logger), logger, "qbittorrent");
        var polled = Assert.Single(await workflow.GetQueueAsync(client, ["exact"]));
        var resolved = await new QbittorrentImportItemResolver(logger).GetImportItemAsync(
            client, new Download { Id = "download" }, new QueueItem { Id = "exact", Status = "completed" });

        Assert.Equal(eligible ? 45 : 0, polled.SourceFiles!.Count);
        Assert.Equal(eligible ? 45 : 0, resolved.SourceFiles?.Count ?? 0);
    }

    [Fact]
    public async Task Recovery_ModernExplicitDeselectionRemainsExcludedWithPayloadOnDisk()
    {
        await using var server = await StartServerAsync("modern-selection", nativeVersion: "2.0.143.0");
        var client = CreateClient(server);
        var resolver = new QbittorrentImportItemResolver(NullLogger.Instance);
        var before = await resolver.GetImportItemAsync(client,
            new Download { Id = "download" }, new QueueItem { Id = "exact", Status = "completed" });
        Assert.Equal(45, before.SourceFiles!.Count);

        using var http = new HttpClient();
        using var response = await http.PostAsync(server.Urls.Single() + "/api/v2/torrents/filePrio",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["hash"] = "exact", ["id"] = "0", ["priority"] = "0" }));
        response.EnsureSuccessStatusCode();

        var after = await resolver.GetImportItemAsync(client,
            new Download { Id = "download" }, new QueueItem { Id = "exact", Status = "completed" });
        Assert.True(after.SourceFiles == null || after.SourceFiles.Count == 0);
        Assert.True(string.IsNullOrEmpty(after.ContentPath));
        Assert.All(before.SourceFiles, file => Assert.True(File.Exists(file)));
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("native-malformed")]
    [InlineData("native-unavailable")]
    public async Task Recovery_NativeFailureNeverEnablesOverride(string scenario)
    {
        await using var server = await StartServerAsync(scenario);
        var result = await new QbittorrentImportItemResolver(NullLogger.Instance).GetImportItemAsync(
            CreateClient(server), new Download { Id = "download" }, new QueueItem { Id = "exact", Status = "completed" });
        Assert.True(result.SourceFiles == null || result.SourceFiles.Count == 0);
    }

    [Theory]
    [InlineData("2.0.129")]
    [InlineData("2.0.143")]
    public async Task Recovery_EmptySelectionNeverCreatesImportSources(string version)
    {
        await using var server = await StartServerAsync("empty", nativeVersion: version);
        var result = await new QbittorrentImportItemResolver(NullLogger.Instance).GetImportItemAsync(
            CreateClient(server), new Download { Id = "download" }, new QueueItem { Id = "exact", Status = "completed" });
        Assert.True(result.SourceFiles == null || result.SourceFiles.Count == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_NativeTimeoutFailsClosedAndCallerCancellationPropagates(bool cancelCaller)
    {
        await using var server = await StartServerAsync("native-stalled");
        using var http = new HttpClient { Timeout = cancelCaller ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100) };
        using var cancellation = new CancellationTokenSource();
        if (cancelCaller) cancellation.CancelAfter(100);
        const string signature = "RealDebridClient <https://github.com/rogerfar/rdt-client>";
        var properties = ParseObject(JsonSerializer.Serialize(new { created_by = signature, comment = signature }));
        var task = QbittorrentCompatibilityDetector.CanRecoverLegacyFileMetadataAsync(
            http, server.Urls.Single(), properties, cancellation.Token);
        if (cancelCaller)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        else
        {
            Assert.False(await task);
        }
    }

    private DownloadClientConfiguration CreateClient(WebApplication server) => new()
    {
        Host = "127.0.0.1",
        Port = new Uri(server.Urls.Single()).Port
    };

    private async Task<WebApplication> StartServerAsync(string scenario, bool remoteMapping = false,
        string? nativeVersion = "2.0.116.0")
    {
        var savePath = FileService.GetTempDirectory("payload");
        var bookPath = FileService.GetTempDirectory(Path.Join("payload", "Book"));
        var files = Enumerable.Range(1, 45).Select(index => new
        {
            name = $"Book/{index:00}.mp3",
            priority = 0,
            progress = 0,
            size = 4555207
        }).ToArray();
        foreach (var file in files)
        {
            await FileService.GetFileAsync(bookPath, Path.GetFileName(file.name));
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapPost("/api/v2/auth/login", () => "Ok.");
        app.MapGet("/api/settings/version", async (CancellationToken ct) =>
        {
            if (scenario == "native-stalled") await Task.Delay(Timeout.Infinite, ct);
            return scenario switch
            {
                "forged" => Results.StatusCode(404),
                "native-malformed" => Results.Text("<html>qBittorrent</html>"),
                "native-unavailable" => Results.StatusCode(503),
                _ => Results.Json(new { version = nativeVersion })
            };
        });
        var selected = scenario == "modern-selection";
        app.MapPost("/api/v2/torrents/filePrio", () => { selected = false; return Results.Ok(); });
        app.MapGet("/api/v2/torrents/files", () => Results.Json(files.Where(_ => scenario != "empty").Select(file => new
        {
            file.name,
            priority = selected ? 1 : 0,
            progress = selected ? 1 : 0,
            file.size
        })));
        const string signature = "RealDebridClient <https://github.com/rogerfar/rdt-client>";
        app.MapGet("/api/v2/torrents/properties", () => Results.Json(new
        {
            save_path = remoteMapping ? "/remote/downloads" : savePath,
            created_by = scenario == "ordinary" ? "qBittorrent" : signature,
            comment = scenario == "ordinary" ? "" : signature
        }));
        app.MapGet("/api/v2/torrents/info", () =>
        {
            if (scenario == "unavailable") return Results.StatusCode(503);
            if (scenario == "malformed") return Results.Text("{");
            var torrent = new
            {
                hash = scenario == "foreign" ? "foreign" : "exact",
                state = "pausedUP",
                progress = scenario == "incomplete" ? 0.5 : 1,
                save_path = remoteMapping ? "/remote/downloads" : savePath,
                content_path = remoteMapping ? "/remote/downloads/Book" : bookPath
            };
            return Results.Json(scenario == "duplicate" ? new[] { torrent, torrent } : new[] { torrent });
        });
        await app.StartAsync();
        return app;
    }

    private static Dictionary<string, JsonElement> ParseObject(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static List<Dictionary<string, JsonElement>> ParseFiles(string json) =>
        JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;
}
