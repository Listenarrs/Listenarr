/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Net;
using System.Text;
using System.Text.Json;
using Listenarr.Infrastructure.Downloads.DirectDownload.Sources;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.DirectDownload;

[Trait("Name", "DirectDownloadProcessorTests")]
[Trait("Category", "DirectDownloadProcessor")]
public sealed class DirectDownloadProcessorTests : BaseTests
{
    private const string PolicyKey = "TestPolicy";
    private const string TrustedInitialUrl = "http://93.184.216.34/alice.m4b";
    private const string TrustedRedirectUrl = "http://93.184.216.34/files/alice.m4b";

    [Fact]
    public async Task ProcessDownloadAsync_DownloadsTrustedPolicyFile_ThenQueuesImport()
    {
        var downloadId = $"ddl-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes("audio payload");
        var jobService = new Mock<IDownloadProcessingJobService>();
        jobService.Setup(service => service.EnqueueAsync(It.IsAny<Download>()))
            .ReturnsAsync("job-1");

        var requestedUris = new List<string>();
        using var httpClient = new HttpClient(new DelegatingHandlerMock((request, _) =>
        {
            requestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            if (requestedUris.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri(TrustedRedirectUrl) }
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
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));

        var download = await _downloadRepository.AddAsync(CreateQueuedDirectDownload(downloadId));
        var processor = _provider.GetRequiredService<IDirectDownloadProcessor>();
        var appPathService = _provider.GetRequiredService<IApplicationPathService>();

        try
        {
            await processor.ProcessDownloadAsync(download, CancellationToken.None);

            Assert.Equal(DownloadStatus.Completed, download.Status);
            Assert.Equal(100, download.Progress);
            Assert.Equal(bytes.Length, download.DownloadedSize);
            Assert.Equal(bytes.Length, download.TotalSize);
            Assert.True(File.Exists(download.DownloadPath));
            Assert.StartsWith(
                appPathService.ResolveFromConfig("downloads", "direct", downloadId),
                download.DownloadPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(download.DownloadPath));
            Assert.Equal(new[] { TrustedInitialUrl, TrustedRedirectUrl }, requestedUris);

            jobService.Verify(service => service.EnqueueAsync(
                It.Is<Download>(d => d.Id == downloadId && d.Status == DownloadStatus.Completed)), Times.Once);
        }
        finally
        {
            DeleteStagingDirectory(download.DownloadPath);
        }
    }

    [Fact]
    public async Task ProcessDownloadAsync_PrivateResolvedTarget_FailsWithoutHttpRequest()
    {
        var jobService = new Mock<IDownloadProcessingJobService>();
        var requestedUris = new List<string>();
        using var httpClient = new HttpClient(new DelegatingHandlerMock((request, _) =>
        {
            requestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("DirectDownload"))
            .Returns(httpClient);

        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));

        var download = CreateQueuedDirectDownload($"ddl-{Guid.NewGuid():N}", "http://127.0.0.1/alice.m4b");
        await _downloadRepository.AddAsync(download);

        var processor = _provider.GetRequiredService<IDirectDownloadProcessor>();

        await processor.ProcessDownloadAsync(download, CancellationToken.None);

        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("private or loopback", download.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(requestedUris);
        jobService.Verify(service => service.EnqueueAsync(It.IsAny<Download>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDownloadAsync_MissingPolicyKey_FailsWithoutHttpRequest()
    {
        var jobService = new Mock<IDownloadProcessingJobService>();
        var httpFactory = new Mock<IHttpClientFactory>();

        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));

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
                Headers = { Location = new Uri("http://93.184.216.35/evil.m4b") }
            });
        }));

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("DirectDownload"))
            .Returns(httpClient);

        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));

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
            DeleteStagingDirectory(download.DownloadPath);
        }
    }

    [Fact]
    public async Task ProcessDownloadAsync_ExistingFinalFile_ReplacesWithCompletedPartialFile()
    {
        var downloadId = $"ddl-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes("new audio payload");
        var jobService = new Mock<IDownloadProcessingJobService>();
        jobService.Setup(service => service.EnqueueAsync(It.IsAny<Download>()))
            .ReturnsAsync("job-1");

        using var httpClient = new HttpClient(new DelegatingHandlerMock((_, _) =>
        {
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
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));

        var download = await _downloadRepository.AddAsync(CreateQueuedDirectDownload(downloadId));
        var processor = _provider.GetRequiredService<IDirectDownloadProcessor>();
        var appPathService = _provider.GetRequiredService<IApplicationPathService>();
        var expectedFinalPath = appPathService.ResolveFromConfig("downloads", "direct", downloadId, "alice.m4b");
        Directory.CreateDirectory(Path.GetDirectoryName(expectedFinalPath)!);
        await File.WriteAllTextAsync(expectedFinalPath, "old payload");

        try
        {
            await processor.ProcessDownloadAsync(download, CancellationToken.None);

            Assert.Equal(DownloadStatus.Completed, download.Status);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(expectedFinalPath));
            Assert.Equal(expectedFinalPath, download.DownloadPath);
        }
        finally
        {
            DeleteStagingDirectory(expectedFinalPath);
        }
    }

    [Fact]
    public async Task ProcessDownloadAsync_ArtifactBatch_DownloadsEveryFileThenQueuesImport()
    {
        // Given
        var downloadId = $"ddl-{Guid.NewGuid():N}";
        var firstBytes = Encoding.UTF8.GetBytes("chapter one");
        var secondBytes = Encoding.UTF8.GetBytes("chapter two");
        var requestedUris = new List<string>();
        var jobService = new Mock<IDownloadProcessingJobService>();
        jobService.Setup(service => service.EnqueueAsync(It.IsAny<Download>()))
            .ReturnsAsync("job-1");
        using var httpClient = new HttpClient(new DelegatingHandlerMock((request, _) =>
        {
            requestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            var bytes = request.RequestUri?.AbsolutePath.EndsWith("chapter-01.mp3", StringComparison.Ordinal) == true
                ? firstBytes
                : secondBytes;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }));
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("DirectDownload"))
            .Returns(httpClient);
        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));
        var download = CreateQueuedDirectDownload(downloadId);
        SetArtifactPlan(
            download,
            new("http://93.184.216.34/chapter-01.mp3", "chapter-01.mp3", firstBytes.Length, DirectDownloadArtifactPackaging.File),
            new("http://93.184.216.34/chapter-02.mp3", "chapter-02.mp3", secondBytes.Length, DirectDownloadArtifactPackaging.File));
        await _downloadRepository.AddAsync(download);

        // When
        await _provider.GetRequiredService<IDirectDownloadProcessor>()
            .ProcessDownloadAsync(download, CancellationToken.None);

        // Then
        try
        {
            Assert.Equal(DownloadStatus.Completed, download.Status);
            Assert.True(Directory.Exists(download.DownloadPath));
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(Path.Combine(download.DownloadPath, "chapter-01.mp3")));
            Assert.Equal(secondBytes, await File.ReadAllBytesAsync(Path.Combine(download.DownloadPath, "chapter-02.mp3")));
            Assert.Equal(firstBytes.Length + secondBytes.Length, download.DownloadedSize);
            Assert.Equal(2, requestedUris.Count);
            jobService.Verify(service => service.EnqueueAsync(
                It.Is<Download>(item => item.Id == downloadId)), Times.Once);
        }
        finally
        {
            DeleteStagingDirectory(download.DownloadPath);
        }
    }

    [Fact]
    public async Task ProcessDownloadAsync_ExpectedArtifactSizeMismatch_FailsDeletesStagingAndSkipsImport()
    {
        // Given
        var downloadId = $"ddl-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes("short payload");
        var jobService = new Mock<IDownloadProcessingJobService>();
        using var httpClient = new HttpClient(new DelegatingHandlerMock((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }));
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("DirectDownload"))
            .Returns(httpClient);
        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));
        var download = CreateQueuedDirectDownload(downloadId);
        SetArtifactPlan(
            download,
            new PersistedDirectDownloadArtifact(
                "http://93.184.216.34/chapter-01.mp3",
                "chapter-01.mp3",
                bytes.Length + 10,
                DirectDownloadArtifactPackaging.File));
        await _downloadRepository.AddAsync(download);
        var stagingRoot = _provider.GetRequiredService<IApplicationPathService>()
            .ResolveFromConfig("downloads", "direct", downloadId);

        try
        {
            // When
            await _provider.GetRequiredService<IDirectDownloadProcessor>()
                .ProcessDownloadAsync(download, CancellationToken.None);

            // Then
            Assert.Equal(DownloadStatus.Failed, download.Status);
            Assert.Contains("expected", download.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bytes", download.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(stagingRoot));
            jobService.Verify(service => service.EnqueueAsync(It.IsAny<Download>()), Times.Never);
        }
        finally
        {
            DeleteStagingDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task ProcessDownloadAsync_ContentLengthMismatchAfterStream_FailsDeletesStagingAndSkipsImport()
    {
        // Given
        var downloadId = $"ddl-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes("short payload");
        var advertisedLength = bytes.Length + 10;
        var jobService = new Mock<IDownloadProcessingJobService>();
        using var httpClient = new HttpClient(new DelegatingHandlerMock((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ShortStreamContent(bytes, advertisedLength)
            });
        }));
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("DirectDownload"))
            .Returns(httpClient);
        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));
        var download = CreateQueuedDirectDownload(downloadId);
        SetArtifactPlan(
            download,
            new PersistedDirectDownloadArtifact(
                "http://93.184.216.34/chapter-01.mp3",
                "chapter-01.mp3",
                0,
                DirectDownloadArtifactPackaging.File));
        await _downloadRepository.AddAsync(download);
        var stagingRoot = _provider.GetRequiredService<IApplicationPathService>()
            .ResolveFromConfig("downloads", "direct", downloadId);

        try
        {
            // When
            await _provider.GetRequiredService<IDirectDownloadProcessor>()
                .ProcessDownloadAsync(download, CancellationToken.None);

            // Then
            Assert.Equal(DownloadStatus.Failed, download.Status);
            Assert.Contains("expected", download.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("downloaded", download.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bytes", download.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(stagingRoot));
            jobService.Verify(service => service.EnqueueAsync(It.IsAny<Download>()), Times.Never);
        }
        finally
        {
            DeleteStagingDirectory(stagingRoot);
        }
    }

    [Fact]
    public async Task ProcessDownloadAsync_ArtifactBatchFailure_DeletesBatchAndSkipsImport()
    {
        // Given
        var downloadId = $"ddl-{Guid.NewGuid():N}";
        var requestCount = 0;
        var jobService = new Mock<IDownloadProcessingJobService>();
        using var httpClient = new HttpClient(new DelegatingHandlerMock((_, _) =>
        {
            requestCount++;
            return Task.FromResult(requestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("chapter one"))
                }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }));
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient("DirectDownload"))
            .Returns(httpClient);
        Init(services => services
            .WithSingleton<IHttpClientFactory>(httpFactory.Object)
            .WithSingleton<IDownloadProcessingJobService>(jobService.Object)
            .WithSingleton<IDirectDownloadSourcePolicy>(new TestDirectDownloadSourcePolicy()));
        var download = CreateQueuedDirectDownload(downloadId);
        SetArtifactPlan(
            download,
            new("http://93.184.216.34/chapter-01.mp3", "chapter-01.mp3", 11, DirectDownloadArtifactPackaging.File),
            new("http://93.184.216.34/chapter-02.mp3", "chapter-02.mp3", 11, DirectDownloadArtifactPackaging.File));
        await _downloadRepository.AddAsync(download);

        // When
        await _provider.GetRequiredService<IDirectDownloadProcessor>()
            .ProcessDownloadAsync(download, CancellationToken.None);

        // Then
        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.False(Directory.Exists(download.DownloadPath));
        Assert.Equal(2, requestCount);
        jobService.Verify(service => service.EnqueueAsync(It.IsAny<Download>()), Times.Never);
    }

    private static Download CreateQueuedDirectDownload(
        string downloadId,
        string originalUrl = TrustedInitialUrl) => new()
        {
            Id = downloadId,
            AudiobookId = 77,
            Title = "Alice in Wonderland",
            Artist = "Lewis Carroll",
            Album = "Alice in Wonderland",
            OriginalUrl = originalUrl,
            DownloadClientId = DirectDownloadMetadataKeys.ClientId,
            Status = DownloadStatus.Queued,
            StartedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                [DirectDownloadMetadataKeys.DownloadType] = DirectDownloadMetadataKeys.ClientId,
                [DirectDownloadMetadataKeys.SourcePolicyKey] = PolicyKey
            }
        };

    private static void SetArtifactPlan(
        Download download,
        params PersistedDirectDownloadArtifact[] artifacts)
    {
        download.SetMetadata(
            DirectDownloadMetadataKeys.ArtifactPlan,
            JsonSerializer.Serialize(new PersistedDirectDownloadArtifactPlan(
                PersistedDirectDownloadArtifactPlan.CurrentVersion,
                artifacts)));
        download.TotalSize = artifacts.Sum(artifact => artifact.ExpectedSize);
    }

    private static void DeleteStagingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var stagingRoot = Directory.Exists(path)
            ? path
            : Directory.GetParent(path)?.FullName;
        if (!string.IsNullOrWhiteSpace(stagingRoot) && Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private sealed class ShortStreamContent(byte[] bytes, long advertisedLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = advertisedLength;
            return true;
        }
    }

    private sealed class TestDirectDownloadSourcePolicy : IDirectDownloadSourcePolicy
    {
        public int Priority => 0;
        public string Key => PolicyKey;

        public bool CanPrepare(Indexer indexer, TrustedDownloadCandidate candidate, IReadOnlyList<Uri> uris) => true;

        public bool TryValidateArtifactPlan(IReadOnlyList<Uri> uris, out string error)
        {
            error = string.Empty;
            return true;
        }

        public bool TryValidateInitialUri(Uri uri, out string error)
        {
            error = string.Empty;
            return true;
        }

        public bool TryValidateRedirectUri(Uri uri, Uri previousUri, out string error)
        {
            error = string.Empty;
            return string.Equals(uri.Host, previousUri.Host, StringComparison.OrdinalIgnoreCase);
        }

        public string GetFileName(Uri uri, Download download) => "alice.m4b";
    }
}
