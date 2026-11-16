using Listenarr.Application.Metadata.Extraction;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Metadata.Extraction
{
    [Trait("Name", "MetadataServiceTests")]
    [Trait("Category", "MetadataService")]
    public class MetadataServiceTests : BaseTests
    {
        private readonly string CLIENT_CONFIG_ID = "empty-1";
        private readonly string DOWNLOAD_ID = "dl-1";
        private readonly int AUDIOBOOK_ID = 1;

        [Theory]
        [InlineData("Book.Final.m4b", "Book.Final", "M4B")]
        [InlineData("BOOK.MP3", "BOOK", "MP3")]
        [InlineData("Bøøk", "Bøøk", "")]
        public async Task ExtractFileMetadataAsync_FfprobeReturnsNoResult_UsesPublicIdentity(
            string publicName,
            string expectedTitle,
            string expectedFormat)
        {
            var readPath = "/proc/123/fd/42";
            var publicPath = Path.Join("library", publicName);
            var ffmpeg = new Mock<IFfmpegService>();
            ffmpeg.Setup(service => service.GetFfprobePathAsync())
                .ReturnsAsync("ffprobe");
            ffmpeg.Setup(service => service.RunFfprobeAsync(
                    It.Is<MetadataFileSource>(source =>
                        source.ReadPath == readPath
                        && source.PublicPath == publicPath)))
                .ReturnsAsync((AudioMetadata)null!);
            var service = CreateMetadataService(ffmpeg.Object);

            var metadata = await service.ExtractFileMetadataAsync(
                new MetadataFileSource(readPath, publicPath));

            Assert.NotNull(metadata);
            Assert.Equal(expectedTitle, metadata.Title);
            Assert.Equal(expectedFormat, metadata.Format);
            ffmpeg.Verify(service => service.RunFfprobeAsync(
                It.Is<MetadataFileSource>(source =>
                    source.ReadPath == readPath
                    && source.PublicPath == publicPath)), Times.Once);
        }

        [LinuxFact]
        public async Task ExtractFileMetadataAsync_LinuxPinnedDescriptor_PublicPathRenameDoesNotChangeFallbackIdentity()
        {
            var sourceDirectory = FileService.GetTempDirectory(
                "metadata-public-identity-source");
            var destinationDirectory = FileService.GetTempDirectory(
                "metadata-public-identity-destination");
            var source = await FileService.GetFileAsync(
                sourceDirectory,
                "Source.m4b",
                "audio");
            var destination = Path.Join(
                destinationDirectory,
                "Public.Name.M4B");
            var movedDestination = Path.Join(
                destinationDirectory,
                "renamed-after-lease.bin");
            var mover = _provider.GetRequiredService<FileMover>();
            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.Copy,
                source,
                destination,
                Guid.NewGuid());
            Assert.NotNull(lease);
            Assert.StartsWith(
                $"/proc/{Environment.ProcessId}/fd/",
                lease.MetadataPath,
                StringComparison.Ordinal);
            File.Move(destination, movedDestination);

            var ffmpeg = new Mock<IFfmpegService>();
            ffmpeg.Setup(service => service.GetFfprobePathAsync())
                .ReturnsAsync("ffprobe");
            ffmpeg.Setup(service => service.RunFfprobeAsync(
                    It.Is<MetadataFileSource>(source =>
                        source.ReadPath == lease.MetadataPath
                        && source.PublicPath == lease.PublicPath)))
                .ReturnsAsync((AudioMetadata)null!);
            var service = CreateMetadataService(ffmpeg.Object);

            var metadata = await service.ExtractFileMetadataAsync(
                new MetadataFileSource(lease.MetadataPath, lease.PublicPath));

            Assert.NotNull(metadata);
            Assert.Equal("Public.Name", metadata.Title);
            Assert.Equal("M4B", metadata.Format);
            ffmpeg.Verify(
                service => service.RunFfprobeAsync(
                    It.Is<MetadataFileSource>(source =>
                        source.ReadPath == lease.MetadataPath
                        && source.PublicPath == lease.PublicPath)),
                Times.Once);
        }

        [Fact]
        public async Task ExtractFileMetadataAsync_FfprobeThrows_UsesPublicIdentity()
        {
            var readPath = "/proc/123/fd/99";
            var publicPath = Path.Join("library", "Public.Name.M4B");
            var ffmpeg = new Mock<IFfmpegService>();
            ffmpeg.Setup(service => service.GetFfprobePathAsync())
                .ReturnsAsync("ffprobe");
            ffmpeg.Setup(service => service.RunFfprobeAsync(
                    It.Is<MetadataFileSource>(source =>
                        source.ReadPath == readPath
                        && source.PublicPath == publicPath)))
                .ThrowsAsync(new InvalidOperationException("ffprobe failed"));
            var service = CreateMetadataService(ffmpeg.Object);

            var metadata = await service.ExtractFileMetadataAsync(
                new MetadataFileSource(readPath, publicPath));

            Assert.NotNull(metadata);
            Assert.Equal("Public.Name", metadata.Title);
            Assert.Equal("M4B", metadata.Format);
            ffmpeg.Verify(service => service.RunFfprobeAsync(
                It.Is<MetadataFileSource>(source =>
                    source.ReadPath == readPath
                    && source.PublicPath == publicPath)), Times.Once);
        }

        [Fact]
        [Trait("Method", "FetchMetadataAsync")]
        public async Task FetchMetadataAsync()
        {
            var sourceDirectory = FileService.GetTempDirectory("FetchMetadataAsync");
            var filePath = await FileService.GetFileAsync(sourceDirectory, "03 - Seconde Fondation Isaac Asimov.withmetadata.mp3");

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId(CLIENT_CONFIG_ID)
                .Build());

            var download = new DownloadBuilder()
                .WithId(DOWNLOAD_ID)
                .WithDownloadClientConfiguration(client)
                .WithUploader("AnotherOneBiteTheDust")
                .WithProtocol(DownloadProtocol.Torrent)
                .Build();

            var audiobook = new AudiobookBuilder()
                .WithId(AUDIOBOOK_ID)
                .WithTitle("Seconde Fondation")
                .WithAuthor("Isaac Asimov")
                .WithPublishedDate(new DateOnly(1996, 6, 1))
                .WithSeries("Le Cycle de Fondation")
                .Build();

            await _audiobookRepository.AddAsync(audiobook);
            await _downloadRepository.AddAsync(download);

            var job = new DownloadProcessingJob
            {
                SourcePath = filePath
            };

            var metadataService = _provider.GetRequiredService<IMetadataService>();
            var metadata = await metadataService.FetchMetadataAsync(job, download, audiobook, default);

            Assert.NotNull(metadata);
            Assert.Equal("Le Cycle de Fondation", metadata.Series);
            Assert.Equal("Seconde Fondation", metadata.Title);
            Assert.Equal("Isaac Asimov", metadata.Artist);
            Assert.Equal("Isaac Asimov", metadata.AlbumArtist);
            Assert.Equal(1996, metadata.Year);
            Assert.Equal(3, metadata.TrackNumber);
            Assert.Equal(1, metadata.DiscNumber);
        }

        private const string CoverAsin = "B008DFUGCQ";
        private const string AbsoluteCoverUrl = "https://images.example.com/cover.jpg";

        [Fact]
        public async Task WriteImportTagsAsync_WhenCoverArtIsDisabled_WritesTheAsinAndNoArtwork()
        {
            var writer = new Mock<IAudioTagWriter>();
            var imageCache = new Mock<IImageCacheService>();
            var service = CreateTagWritingService(
                writer.Object, new HttpClientGuardHandler(), enableCoverArt: false, imageCache.Object);

            await service.WriteImportTagsAsync(
                Mock.Of<IAudiobookFileRegistrationLease>(),
                CoverAsin,
                AbsoluteCoverUrl);

            writer.Verify(
                w => w.WriteTagsAsync(
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    CoverAsin,
                    null),
                Times.Once);

            // The setting is checked before the image is loaded, so a disabled instance does
            // not pay for a fetch whose result it would throw away.
            imageCache.Verify(cache => cache.GetImageBytesAsync(It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData(AbsoluteCoverUrl)]
        [InlineData("config/cache/images/library/B008DFUGCQ.jpg")]
        public async Task WriteImportTagsAsync_WhenCoverArtIsEnabled_EmbedsTheImageTheCacheReturns(
            string storedCover)
        {
            // Library-added books store the published cover as a content-root-relative path
            // rather than a URL, so both shapes must reach the image cache and not an
            // unconfigured HttpClient.
            var writer = new Mock<IAudioTagWriter>();
            var service = CreateTagWritingService(
                writer.Object,
                new HttpClientGuardHandler(),
                enableCoverArt: true,
                ImageCacheReturning(storedCover, JpegBytes));

            await service.WriteImportTagsAsync(
                Mock.Of<IAudiobookFileRegistrationLease>(),
                CoverAsin,
                storedCover);

            writer.Verify(
                w => w.WriteTagsAsync(
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    CoverAsin,
                    It.Is<AudioCoverArt>(art => art != null && art.MimeType == "image/jpeg")),
                Times.Once);
        }

        [Fact]
        public async Task WriteImportTagsAsync_WhenTheImageIsNotAnImage_StillWritesTheAsin()
        {
            // An image host answering with an error page must not cost the import its ASIN
            // tag, and must not embed the error page as a cover.
            var writer = new Mock<IAudioTagWriter>();
            var service = CreateTagWritingService(
                writer.Object,
                new HttpClientGuardHandler(),
                enableCoverArt: true,
                ImageCacheReturning(AbsoluteCoverUrl, "<html>nope</html>"u8.ToArray()));

            await service.WriteImportTagsAsync(
                Mock.Of<IAudiobookFileRegistrationLease>(),
                CoverAsin,
                AbsoluteCoverUrl);

            writer.Verify(
                w => w.WriteTagsAsync(
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    CoverAsin,
                    null),
                Times.Once);
        }

        [Fact]
        public async Task WriteImportTagsAsync_WhenTheCacheCannotLoadTheImage_StillWritesTheAsin()
        {
            var writer = new Mock<IAudioTagWriter>();
            var service = CreateTagWritingService(
                writer.Object,
                new HttpClientGuardHandler(),
                enableCoverArt: true,
                ImageCacheReturning(AbsoluteCoverUrl, null));

            await service.WriteImportTagsAsync(
                Mock.Of<IAudiobookFileRegistrationLease>(),
                CoverAsin,
                AbsoluteCoverUrl);

            writer.Verify(
                w => w.WriteTagsAsync(
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    CoverAsin,
                    null),
                Times.Once);
        }

        // The import path must not touch the typed HttpClient: it has no validation and
        // follows redirects.
        private sealed class HttpClientGuardHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("The import path made an unvalidated request.");
        }

        [Fact]
        public async Task DownloadCoverArtAsync_BodyAtTheLimit_IsReturned()
        {
            var body = new byte[MetadataService.MaxCoverArtBytes];
            var service = CreateTagWritingService(
                Mock.Of<IAudioTagWriter>(),
                new StaticImageHandler(body),
                enableCoverArt: true);

            var bytes = await service.DownloadCoverArtAsync("https://images.example.com/cover.jpg");

            Assert.Equal(body.Length, bytes?.Length);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task DownloadCoverArtAsync_BodyOverTheLimit_IsRefused(bool declareLength)
        {
            // Without a declared length (chunked responses) the read itself has to stop.
            var body = new byte[MetadataService.MaxCoverArtBytes + 1];
            var service = CreateTagWritingService(
                Mock.Of<IAudioTagWriter>(),
                new StaticImageHandler(body, declareLength),
                enableCoverArt: true);

            var bytes = await service.DownloadCoverArtAsync("https://images.example.com/cover.jpg");

            Assert.Null(bytes);
        }

        [Fact]
        public async Task DownloadCoverArtAsync_DeclaredLengthOverTheLimit_IsRefusedWithoutReadingTheBody()
        {
            var handler = new DeclaredOversizeHandler();
            var service = CreateTagWritingService(
                Mock.Of<IAudioTagWriter>(),
                handler,
                enableCoverArt: true);

            var bytes = await service.DownloadCoverArtAsync("https://images.example.com/cover.jpg");

            Assert.Null(bytes);
            Assert.False(handler.Body.WasRead);
        }

        private sealed class DeclaredOversizeHandler : HttpMessageHandler
        {
            public ReadTrackingStream Body { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var content = new StreamContent(Body);
                content.Headers.ContentLength = MetadataService.MaxCoverArtBytes + 1L;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = content
                });
            }
        }

        private sealed class ReadTrackingStream : MemoryStream
        {
            public bool WasRead { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                WasRead = true;
                return base.Read(buffer, offset, count);
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                WasRead = true;
                return base.ReadAsync(buffer, cancellationToken);
            }
        }

        // A MemoryStream that refuses to report its length, so StreamContent cannot declare one.
        private sealed class UndeclaredLengthStream(byte[] body) : MemoryStream(body)
        {
            public override bool CanSeek => false;
        }

        private sealed class StaticImageHandler(byte[]? body = null, bool declareLength = true) : HttpMessageHandler
        {
            private readonly byte[] _body = body ?? [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0];

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                HttpContent content = declareLength
                    ? new ByteArrayContent(_body)
                    : new StreamContent(new UndeclaredLengthStream(_body));
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = content
                });
            }
        }

        private static MetadataService CreateTagWritingService(
            IAudioTagWriter writer,
            HttpMessageHandler handler,
            bool enableCoverArt,
            IImageCacheService? imageCache = null)
        {
            var configuration = new Mock<IConfigurationService>();
            configuration.Setup(service => service.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    EmbedCoverArtInAudioFiles = enableCoverArt
                });

            return new MetadataService(
                new HttpClient(handler),
                configuration.Object,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataService>.Instance,
                Mock.Of<IFfmpegService>(),
                writer,
                Mock.Of<IFileSystem>(),
                imageCache ?? Mock.Of<IImageCacheService>());
        }

        private static IImageCacheService ImageCacheReturning(string reference, byte[]? bytes)
        {
            var imageCache = new Mock<IImageCacheService>();
            imageCache.Setup(cache => cache.GetImageBytesAsync(reference)).ReturnsAsync(bytes);
            return imageCache.Object;
        }

        private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0];

        private static MetadataService CreateMetadataService(
            IFfmpegService ffmpegService)
        {
            var configuration = new Mock<IConfigurationService>();
            configuration.Setup(service => service.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    EnableMetadataProcessing = true
                });
            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(system => system.FileExists(It.IsAny<string>()))
                .Returns(true);

            return new MetadataService(
                new HttpClient(),
                configuration.Object,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MetadataService>.Instance,
                ffmpegService,
                Mock.Of<IAudioTagWriter>(),
                fileSystem.Object,
                Mock.Of<IImageCacheService>());
        }
    }
}
