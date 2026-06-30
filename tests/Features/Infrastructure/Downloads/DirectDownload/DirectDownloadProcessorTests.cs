// Listenarr - Audiobook Management System
// Copyright (C) 2024-2026 Listenarr Contributors

using System.Net;
using System.Text;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.DirectDownload;

[Trait("Name", "DirectDownloadProcessorTests")]
[Trait("Category", "DirectDownloadProcessor")]
public sealed class DirectDownloadProcessorTests : BaseTests
{
    [Fact]
    public async Task ProcessDownloadAsync_DownloadsTrustedPolicyFile_ThenQueuesImport()
    {
        var downloadId = $"ddl-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes("audio payload");
        var jobService = new Mock<IDownloadProcessingJobService>();
        jobService.Setup(service => service.EnqueueAsync(It.IsAny<Download>()))
            .ReturnsAsync("job-1");

        var trustedRedirectUrl = "https://ia800000.us.archive.org/0/items/alice_in_wonderland/alice.m4b";
        var requestedUris = new List<string>();
        using var httpClient = new HttpClient(new DelegatingHandlerMock((request, _) =>
        {
            requestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            if (requestedUris.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri(trustedRedirectUrl) }
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(response);
        }));

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("DirectDownload"))
            .Returns(httpClient);

        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object));

        var download = await _downloadRepository.AddAsync(CreateQueuedDirectDownload(downloadId));
        var processor = _provider.GetRequiredService<IDirectDownloadProcessor>();

        try
        {
            await processor.ProcessDownloadAsync(download, CancellationToken.None);

            Assert.Equal(DownloadStatus.Completed, download.Status);
            Assert.Equal(100, download.Progress);
            Assert.Equal(bytes.Length, download.DownloadedSize);
            Assert.Equal(bytes.Length, download.TotalSize);
            Assert.True(File.Exists(download.DownloadPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(download.DownloadPath));
            Assert.Equal(new[] { download.OriginalUrl, trustedRedirectUrl }, requestedUris);

            jobService.Verify(service => service.EnqueueAsync(
                It.Is<Download>(d => d.Id == downloadId && d.Status == DownloadStatus.Completed)), Times.Once);
        }
        finally
        {
            DeleteStagingDirectory(downloadId);
        }
    }

    [Fact]
    public async Task ProcessDownloadAsync_MissingPolicyKey_FailsWithoutHttpRequest()
    {
        var jobService = new Mock<IDownloadProcessingJobService>();
        var httpFactory = new Mock<IHttpClientFactory>();

        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object));

        var download = CreateQueuedDirectDownload($"ddl-{Guid.NewGuid():N}");
        download.Metadata.Remove(DirectDownloadMetadataKeys.SourcePolicyKey);
        await _downloadRepository.AddAsync(download);

        var processor = _provider.GetRequiredService<IDirectDownloadProcessor>();

        await processor.ProcessDownloadAsync(download, CancellationToken.None);

        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("source policy", download.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        httpFactory.Verify(factory => factory.CreateClient(It.IsAny<string>()), Times.Never);
        jobService.Verify(service => service.EnqueueAsync(It.IsAny<Download>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDownloadAsync_UntrustedRedirect_FailsAndDeletesPartialFile()
    {
        var downloadId = $"ddl-{Guid.NewGuid():N}";
        var jobService = new Mock<IDownloadProcessingJobService>();
        var requestedUris = new List<string>();
        using var httpClient = new HttpClient(new DelegatingHandlerMock((request, _) =>
        {
            requestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://example.com/evil.m4b") }
            });
        }));

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("DirectDownload"))
            .Returns(httpClient);

        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object));

        var download = await _downloadRepository.AddAsync(CreateQueuedDirectDownload(downloadId));
        var processor = _provider.GetRequiredService<IDirectDownloadProcessor>();

        try
        {
            await processor.ProcessDownloadAsync(download, CancellationToken.None);

            Assert.Equal(DownloadStatus.Failed, download.Status);
            Assert.Contains("redirect", download.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(download.DownloadPath + ".partial"));
            Assert.Single(requestedUris);
            jobService.Verify(service => service.EnqueueAsync(It.IsAny<Download>()), Times.Never);
        }
        finally
        {
            DeleteStagingDirectory(downloadId);
        }
    }

    private static Download CreateQueuedDirectDownload(string downloadId) => new()
    {
        Id = downloadId,
        AudiobookId = 77,
        Title = "Alice in Wonderland",
        Artist = "Lewis Carroll",
        Album = "Alice in Wonderland",
        OriginalUrl = "https://archive.org/download/alice_in_wonderland/alice.m4b",
        DownloadClientId = "DDL",
        Status = DownloadStatus.Queued,
        StartedAt = DateTime.UtcNow,
        Metadata = new Dictionary<string, object>
        {
            [DirectDownloadMetadataKeys.DownloadType] = "DDL",
            [DirectDownloadMetadataKeys.SourcePolicyKey] = "InternetArchive"
        }
    };

    private static void DeleteStagingDirectory(string downloadId)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "listenarr-direct-downloads", downloadId);
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
    }
}
