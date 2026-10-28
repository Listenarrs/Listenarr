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
                fileSystem.Object);
        }
    }
}
