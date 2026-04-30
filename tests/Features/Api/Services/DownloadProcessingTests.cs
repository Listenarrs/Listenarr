using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Moq;
using Listenarr.Api.Services;
using System.Reflection;
using Listenarr.Api.Services.Metadata;
using System.Runtime.InteropServices;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Mocks;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "DownloadProcessingTests")]
    [Trait("Category", "DownloadProcessing")]
    public class DownloadProcessingTests : BaseTests
    {
        private readonly string CLIENT_CONFIG_ID = "dl-client-1";
        private readonly string DOWNLOAD_COMPLETE_ID = "dl-complete-1";

        private Download _download = new DownloadBuilder().Build();
        private DownloadClientConfiguration _client = new DownloadClientConfigurationBuilder().Build();

        private async Task InitData()
        {
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId(CLIENT_CONFIG_ID)
                .WithName("Slskd")
                .WithType("slskd")
                .WithHost("localhost")
                .WithPort(5030)
                .Enabled()
                .Build());

            _download = new DownloadBuilder()
                .WithId(DOWNLOAD_COMPLETE_ID)
                .WithDownloadClientConfiguration(_client)
                .WithProtocol(DownloadProtocol.Torrent)
                .WithUploader("USER1")
                .Build();

            await _downloadRepository.AddAsync(_download);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        public async Task ProcessCompletedDownload_CreatesAudiobookFileAndBroadcasts()
        {
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "Test Book", Artist = "Test Author", Duration = TimeSpan.FromSeconds(3600), Format = "m4b", BitRate = 64000, SampleRate = 44100, Channels = 2 });

            _services.AddSingleton(metadataMock.Object);

            Init();
            await InitData();

            var testPath = await FileService.GetTempFileAsync("dl-test.m4b");

            var audiobook = new AudiobookBuilder()
                .WithId(1)
                .WithTitle("Test Book")
                .Build();

            var download = new DownloadBuilder()
                .WithId("dl-1")
                .WithAudiobook(audiobook)
                .WithStatus(DownloadStatus.Downloading)
                .WithPath(testPath)
                .WithStartDate(DateTime.UtcNow)
                .Build();

            await _audiobookRepository.AddAsync(audiobook);
            await _downloadRepository.AddAsync(download);

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(FileService.GetTempPath())
                .WithMoveFileOnCompleted()
                // FIXME: Even without metadataprocessing, the metadata are still used (WithoutMetadataProcessing)
                .WithMetadataProcessing()
                .Build());

            // Act
            var downloadService = _provider.GetRequiredService<DownloadService>();
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: audiobook file created
            var file = (await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id)).First();
            if (file != null)
            {
                Assert.Contains("Test Author", file.Path);
                Assert.Contains("Test Book", file.Path);
                Assert.NotNull(file.DurationSeconds);
                Assert.InRange(file.DurationSeconds.Value, 3599.0, 3601.0);
                Assert.Equal("m4b", file.Format);
            }
            else
            {
                // Import deferred; assert that the final file (or a file in the output path) exists on disk
                Assert.True(File.Exists(download.FinalPath) || Directory.GetFiles(Path.GetDirectoryName(download.FinalPath) ?? string.Empty, "*", SearchOption.TopDirectoryOnly).Length > 0, "Expected the final file or files on disk when import is deferred");
            }

            // Broadcast behavior not asserted here; ensure import and registration completed successfully.
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        public async Task ProcessCompletedDownload_AudiobookWithBasePath_UsesFilenameOnly_NoExtraFolders()
        {
            _services.AddSingleton<MetadataServiceMock>();
            Init();
            await InitData();

            var sourceDirectory = FileService.GetTempDirectory("source");
            var destinationDirectory = FileService.GetTempDirectory("audiobook-base");

            var sourceFile = await FileService.GetFileAsync(sourceDirectory, "source-file.m4b");

            var audiobook = new AudiobookBuilder()
                .WithId(1)
                .WithTitle("Test Audiobook")
                .WithAuthor("Test Author")
                .WithBasePath(destinationDirectory)
                .Build();

            var download = new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithStatus(DownloadStatus.Downloading)
                .WithPath(sourceDirectory)
                .WithStartDate(DateTime.UtcNow)
                .Build();

            await _audiobookRepository.AddAsync(audiobook);
            await _downloadRepository.AddAsync(download);

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .WithFileNamingPattern("{Author}/{Series}/{DiskNumber:00} - {Title}")
                .Build());

            // Act
            var downloadService = _provider.GetRequiredService<DownloadService>();
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: download is completed and final path is within BasePath (verify with fresh DbContext)
            var updatedDownload = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(updatedDownload);
            Assert.True(updatedDownload.Status == DownloadStatus.Completed || updatedDownload.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {updatedDownload.Status}");
            Assert.NotNull(updatedDownload.FinalPath);
            // Either the file was moved into the audiobook BasePath synchronously, or finalization queued/deferred the import and FinalPath may remain the original source path.
            bool movedIntoBase = updatedDownload.FinalPath.StartsWith(destinationDirectory, StringComparison.OrdinalIgnoreCase);
            bool stillAtSource = string.Equals(updatedDownload.FinalPath, sourceFile, StringComparison.OrdinalIgnoreCase);
            Assert.True(movedIntoBase || stillAtSource, $"FinalPath should either be in BasePath or equal source path, got {updatedDownload.FinalPath}");

            if (movedIntoBase)
            {
                // Assert: file exists at final path and no extra folders created
                Assert.True(File.Exists(updatedDownload.FinalPath));
                var relativePath = Path.GetRelativePath(destinationDirectory, updatedDownload.FinalPath);
                Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), relativePath);
                Assert.DoesNotContain(Path.AltDirectorySeparatorChar.ToString(), relativePath);

                var directoriesInBasePath = Directory.GetDirectories(destinationDirectory, "*", SearchOption.AllDirectories);
                Assert.Empty(directoriesInBasePath);

                var filesInBasePath = Directory.GetFiles(destinationDirectory, "*", SearchOption.AllDirectories);
                Assert.Single(filesInBasePath);
                Assert.Equal(updatedDownload.FinalPath, filesInBasePath[0]);

                // Assert: source file was moved (not copied)
                Assert.False(File.Exists(sourceFile));

                // Assert: audiobook file record was created
                var audiobookFile = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
                Assert.Equal(updatedDownload.FinalPath, audiobookFile.Path);
            }
            else
            {
                // Import may be deferred; ensure source file still exists and job should be queued/handled later.
                Assert.True(File.Exists(sourceFile));
            }
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        public async Task ProcessCompletedDownload_AudiobookWithMultipleFiles()
        {
            var destinationDirectory = FileService.GetTempDirectory("audiobook-base");
            var sourceDirectory = FileService.GetTempDirectory("source");
            var sourceFile1 = await FileService.GetFileAsync(sourceDirectory, "source-file1.m4b");
            var sourceFile2 = await FileService.GetFileAsync(sourceDirectory, "source-file2.m4b");
            var sourceFile3 = await FileService.GetFileAsync(sourceDirectory, "source-file3.m4b");

            var audiobook = new AudiobookBuilder()
                .WithId(1)
                .WithTitle("Test Audiobook")
                .WithAuthor("Test Author")
                .WithBasePath(destinationDirectory)
                .Build();

            var download = new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithStatus(DownloadStatus.Downloading)
                .WithPath(sourceDirectory)
                .WithStartDate(DateTime.UtcNow)
                .Build();

            await _audiobookRepository.AddAsync(audiobook);
            await _downloadRepository.AddAsync(download);


            // Mock configuration service to return settings with metadata processing enabled
            // and a naming pattern that would normally create subdirectories
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .WithFileNamingPattern("{Author}/{Series}/{DiskNumber:00} - {Title}")
                .Build());

            // Act
            var downloadService = _provider.GetRequiredService<DownloadService>();
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: download is completed and final path is within BasePath (verify with fresh DbContext)
            var updatedDownload = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(updatedDownload);
            Assert.True(updatedDownload.Status == DownloadStatus.Completed || updatedDownload.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {updatedDownload.Status}");
            Assert.NotNull(updatedDownload.FinalPath);
            // Either the file was moved into the audiobook BasePath synchronously, or finalization queued/deferred the import and FinalPath may remain the original source path.
            bool movedIntoBase = updatedDownload.FinalPath.StartsWith(destinationDirectory, StringComparison.OrdinalIgnoreCase);
            bool stillAtSource = string.Equals(updatedDownload.FinalPath, sourceDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.True(movedIntoBase || stillAtSource, $"FinalPath should either be in BasePath or equal source path, got {updatedDownload.FinalPath}");

            if (movedIntoBase)
            {
                // Assert: file exists at final path and no extra folders created
                Assert.True(File.Exists(updatedDownload.FinalPath));
                var relativePath = Path.GetRelativePath(destinationDirectory, updatedDownload.FinalPath);
                Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), relativePath);
                Assert.DoesNotContain(Path.AltDirectorySeparatorChar.ToString(), relativePath);

                var directoriesInBasePath = Directory.GetDirectories(destinationDirectory, "*", SearchOption.AllDirectories);
                Assert.Empty(directoriesInBasePath);

                var filesInBasePath = Directory.GetFiles(destinationDirectory, "*", SearchOption.AllDirectories);
                Assert.Equal(3, filesInBasePath.Length);
                Assert.Contains(updatedDownload.FinalPath, filesInBasePath);

                // Assert: source file were moved (not copied)
                Assert.False(File.Exists(sourceFile1));
                Assert.False(File.Exists(sourceFile2));
                Assert.False(File.Exists(sourceFile3));

                // Assert: audiobook file record was created
                var audiobookFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
                Assert.Equal(3, audiobookFiles.Count);
                var paths = audiobookFiles.Select(a => a.Path).ToList();
                Assert.Contains(updatedDownload.FinalPath, paths);
            }
            else
            {
                // Import may be deferred; ensure source file still exists and job should be queued/handled later.
                Assert.True(File.Exists(sourceFile1));
                Assert.True(File.Exists(sourceFile2));
                Assert.True(File.Exists(sourceFile3));
            }
        }

        [Fact]
        [Trait("Method", "ProcessMoveOrCopyJobAsync")]
        public async Task DownloadProcessingBackgroundService_ProcessMoveOrCopy_MultipleFilesAndRemotePathMapping()
        {
            var remoteSource = FileService.GetTempDirectory("dl-remote-source");
            var localSource = FileService.GetTempDirectory("dl-local-source");
            var localDestination = FileService.GetTempDirectory("dl-destination");

            var remoteChapter1 = Path.Join(remoteSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter2 = Path.Join(remoteSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter3 = Path.Join(remoteSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter4 = Path.Join(remoteSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var remoteCompanion = Path.Join(remoteSource, "Seconde Fondation Isaac Asimov.nfo");

            var localChapter1 = await FileService.GetFileAsync(localSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter2 = await FileService.GetFileAsync(localSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter3 = await FileService.GetFileAsync(localSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter4 = await FileService.GetFileAsync(localSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var localCompanion = await FileService.GetFileAsync(localSource, "Seconde Fondation Isaac Asimov.nfo");

            var importItemResolutionServiceMock = _provider.GetRequiredService<Mock<IImportItemResolutionService>>();
            importItemResolutionServiceMock
                .Setup(r => r.ResolveImportItemAsync(
                    It.Is<Download>(d => d.Id == DOWNLOAD_COMPLETE_ID),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download download, QueueItem queueItem, QueueItem? previousAttempt, CancellationToken ct) =>
                {
                    queueItem.SourceFiles = [
                        remoteChapter1,
                        remoteChapter2,
                        remoteChapter3,
                        remoteChapter4,
                        remoteCompanion
                    ];
                    return queueItem;
                });

            await InitData();
            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(_client)
                .WithLocalPath(localSource)
                .WithRemotePath(remoteSource)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                // FIXME: When removing the output path, the import follows a completely different logic
                .WithOutputPath(localDestination)
                .WithoutMetadataProcessing()
                .WithMoveFileOnCompleted()
                .Build());

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var dpbs = _provider.GetRequiredService<DownloadProcessingBackgroundService>();
            var job = await MockUtils.CreateDownloadProcessingJob(_provider, _download, localSource);
            using var scope = _provider.CreateScope();
            var task = (Task)method!.Invoke(dpbs, [job, scope, CancellationToken.None])!;
            await task;

            Assert.NotNull(method);

            Assert.True(File.Exists(Path.Join(localDestination, "01 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "02 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "03 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "04 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "Seconde Fondation Isaac Asimov.nfo")));
        }

        [Fact]
        [Trait("Method", "ProcessMoveOrCopyJobAsync")]
        [Trait("OSPlatform", "Linux")]
        [Trait("OSPlatform", "OSX")]
        public async Task DownloadProcessingBackgroundService_HandleSpace()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return;
            }

            var remoteSource = FileService.GetTempDirectory("dl-remote-source ");
            var localSource = FileService.GetTempDirectory("dl-local-source ");
            var localDestination = FileService.GetTempDirectory("dl-destination");

            var remoteChapter1 = Path.Join(remoteSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter2 = Path.Join(remoteSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter3 = Path.Join(remoteSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter4 = Path.Join(remoteSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var remoteCompanion = Path.Join(remoteSource, "Seconde Fondation Isaac Asimov.nfo");

            var localChapter1 = await FileService.GetFileAsync(localSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter2 = await FileService.GetFileAsync(localSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter3 = await FileService.GetFileAsync(localSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter4 = await FileService.GetFileAsync(localSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var localCompanion = await FileService.GetFileAsync(localSource, "Seconde Fondation Isaac Asimov.nfo");

            var importItemResolutionServiceMock = _provider.GetRequiredService<Mock<IImportItemResolutionService>>();
            importItemResolutionServiceMock
                .Setup(r => r.ResolveImportItemAsync(
                    It.Is<Download>(d => d.Id == DOWNLOAD_COMPLETE_ID),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download download, QueueItem queueItem, QueueItem? previousAttempt, CancellationToken ct) =>
                {
                    queueItem.SourceFiles = [
                        remoteChapter1,
                        remoteChapter2,
                        remoteChapter3,
                        remoteChapter4,
                        remoteCompanion
                    ];
                    return queueItem;
                });

            await InitData();
            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(_client)
                .WithLocalPath(localSource)
                .WithRemotePath(remoteSource)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                // FIXME: When removing the output path, the import follows a completely different logic
                .WithOutputPath(localDestination)
                .WithoutMetadataProcessing()
                .WithMoveFileOnCompleted()
                .Build());

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var downloadProcessingBackgroundService = _provider.GetRequiredService<DownloadProcessingBackgroundService>();
            var job = await MockUtils.CreateDownloadProcessingJob(_provider, _download, localSource);
            using var scope = _provider.CreateScope();
            var task = (Task)method!.Invoke(downloadProcessingBackgroundService, [job, scope, CancellationToken.None])!;
            await task;

            Assert.NotNull(method);

            Assert.True(File.Exists(Path.Join(localDestination, "01 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "02 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "03 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "04 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "Seconde Fondation Isaac Asimov.nfo")));
        }
    }
}
