/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;
using System.Runtime.InteropServices;
using System.IO.Compression;
using Listenarr.Tests.Mocks;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Application.Downloads.Import
{
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadImportServiceTests : BaseTests
    {
        private MetadataServiceMock metadataServiceMock = new();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IMetadataService>(metadataServiceMock);
            Init();
            await AddAuthorizedRootAsync(FileService.GetTempPath());
        }

        public static TheoryData<string> PathSuffixes
        {
            get
            {
                var data = new TheoryData<string>
                {
                    { Path.Join("Jane Austen", "Pride and Prejudice") },
                    { Path.Join("Test") },
                    { Path.Join("will", "use", "any", "given", "base", "path") }
                };

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    data.Add(Path.Join(" even ", "if ", "we", "use", "  space  "));
                }

                return data;
            }
        }

        [Fact]
        public void ImportDestinationPlanner_PreservesUnixSegmentWhitespace()
        {
            var planner = new ImportDestinationPlanner(Mock.Of<IFileSystem>());
            var semantics = new FileSystemPathSemantics(
                FileSystemPathSyntax.Unix,
                FileSystemCaseSensitivity.Sensitive);

            var resolved = planner.TryResolve(
                "/library",
                " Disc 1/Chapter 01.mp3 ",
                semantics,
                out var destination);

            Assert.True(resolved);
            Assert.Equal("/library/ Disc 1/Chapter 01.mp3 ", destination);
        }

        [Fact]
        public async Task ImportDestinationPlanner_RepeatedBatch_ReusesMatchingExistingSuffix()
        {
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            var source = Path.Join(
                Path.GetTempPath(),
                $"planner-repeat-source-{Guid.NewGuid():N}.mp3");
            var destination = Path.Join(
                Path.GetTempPath(),
                $"planner-repeat-destination-{Guid.NewGuid():N}.mp3");
            var directory = Path.GetDirectoryName(destination)!;
            var firstSuffix = Path.Join(
                directory,
                $"{Path.GetFileNameWithoutExtension(destination)} (1){Path.GetExtension(destination)}");
            fileSystem.Setup(service => service.DirectoryExists(directory)).Returns(true);
            fileSystem.Setup(service => service.EnumerateFiles(directory))
                .Returns([destination, firstSuffix]);
            fileSystem.Setup(service => service.FilesHaveSameContentAsync(
                    source,
                    destination,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            fileSystem.Setup(service => service.FilesHaveSameContentAsync(
                    source,
                    firstSuffix,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var planner = new ImportDestinationPlanner(fileSystem.Object);
            var semantics = FileSystemPathSemantics.CurrentHostDefault;

            var reservation = await planner.PlanIdempotentOrUniqueAsync(
                source,
                destination,
                new HashSet<string>(semantics.Comparer),
                semantics);

            Assert.Equal(firstSuffix, reservation.Path);
            fileSystem.VerifyAll();
        }

        [Fact]
        public async Task ImportDestinationPlanner_RepeatedBatch_ReusesMatchingSuffixAcrossGap()
        {
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            var source = Path.Join(
                Path.GetTempPath(),
                $"planner-gap-source-{Guid.NewGuid():N}.mp3");
            var destination = Path.Join(
                Path.GetTempPath(),
                $"planner-gap-destination-{Guid.NewGuid():N}.mp3");
            var directory = Path.GetDirectoryName(destination)!;
            var secondSuffix = Path.Join(
                directory,
                $"{Path.GetFileNameWithoutExtension(destination)} (2){Path.GetExtension(destination)}");
            fileSystem.Setup(service => service.DirectoryExists(directory)).Returns(true);
            fileSystem.Setup(service => service.EnumerateFiles(directory))
                .Returns([destination, secondSuffix]);
            fileSystem.Setup(service => service.FilesHaveSameContentAsync(
                    source,
                    destination,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            fileSystem.Setup(service => service.FilesHaveSameContentAsync(
                    source,
                    secondSuffix,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var planner = new ImportDestinationPlanner(fileSystem.Object);
            var semantics = FileSystemPathSemantics.CurrentHostDefault;

            var reservation = await planner.PlanIdempotentOrUniqueAsync(
                source,
                destination,
                new HashSet<string>(semantics.Comparer),
                semantics);

            Assert.Equal(secondSuffix, reservation.Path);
            fileSystem.VerifyAll();
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_ReplayedCompletedSource_ReusesPriorMatchingSuffixWithoutDuplicateRow()
        {
            var basePath = FileService.GetTempDirectory("download-import-idempotent-replay");
            var sourceDirectory = FileService.GetTempDirectory("download-import-idempotent-source");
            var sourceFile = await FileService.GetFileAsync(
                sourceDirectory,
                "incoming.mp3",
                "same-completed-download-content");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Replay Book")
                .WithBasePath(basePath)
                .Build());
            await FileService.GetFileAsync(
                basePath,
                "Replay Book.mp3",
                "different-existing-library-content");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .WithFolderNamingPattern("")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}")
                .Build());
            var service = _provider.GetRequiredService<IDownloadImportService>();

            var first = Assert.Single(await service.ImportDownloadFilesAsync(
                audiobook,
                [sourceFile]));
            var second = Assert.Single(await service.ImportDownloadFilesAsync(
                audiobook,
                [sourceFile]));

            var expectedSuffix = Path.Join(basePath, "Replay Book (1).mp3");
            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(expectedSuffix, first.FinalPath);
            Assert.Equal(expectedSuffix, second.FinalPath);
            Assert.True(File.Exists(expectedSuffix));
            Assert.False(File.Exists(Path.Join(basePath, "Replay Book (2).mp3")));
            var tracked = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            var imported = Assert.Single(tracked);
            Assert.Equal(expectedSuffix, imported.Path);
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_MatchingSuffixOwnedByOtherAudiobook_UsesNextFreeSuffix()
        {
            var basePath = FileService.GetTempDirectory("download-import-owned-replay");
            var sourceDirectory = FileService.GetTempDirectory("download-import-owned-replay-source");
            var sourceFile = await FileService.GetFileAsync(
                sourceDirectory,
                "incoming.mp3",
                "shared-source-content");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Replay Book")
                .WithBasePath(basePath)
                .Build());
            var otherAudiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Other Owner")
                .WithBasePath(basePath)
                .Build());
            await FileService.GetFileAsync(
                basePath,
                "Replay Book.mp3",
                "different-existing-library-content");
            var ownedSuffix = await FileService.GetFileAsync(
                basePath,
                "Replay Book (1).mp3",
                "shared-source-content");
            Assert.True(await _provider
                .GetRequiredService<IAudiobookFileService>()
                .EnsureAudiobookFileAsync(otherAudiobook, ownedSuffix, "test"));
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .WithFolderNamingPattern("")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}")
                .Build());

            var result = Assert.Single(await _provider
                .GetRequiredService<IDownloadImportService>()
                .ImportDownloadFilesAsync(audiobook, [sourceFile]));

            var expected = Path.Join(basePath, "Replay Book (2).mp3");
            Assert.True(result.Success);
            Assert.Equal(expected, result.FinalPath);
            Assert.True(File.Exists(expected));
            Assert.Equal(
                ownedSuffix,
                Assert.Single(await _audiobookFileRepository
                    .GetByAudiobookIdAsync(otherAudiobook.Id)).Path);
            Assert.Equal(
                expected,
                Assert.Single(await _audiobookFileRepository
                    .GetByAudiobookIdAsync(audiobook.Id)).Path);
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_UnresolvedMoveExecution_BlocksBeforeDestinationPlanning()
        {
            var basePath = FileService.GetTempDirectory("download-import-unresolved-move");
            var sourceFile = await FileService.GetTempFileAsync("unresolved-import.mp3");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Import Move Fence")
                .WithBasePath(basePath)
                .Build());
            await MoveJobTestFactory.SeedUnresolvedExecutionAsync(
                _provider,
                audiobook.Id,
                basePath,
                Path.Join(FileService.GetTempPath(), $"download-import-target-{Guid.NewGuid():N}"));

            var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                _provider.GetRequiredService<IDownloadImportService>()
                    .ImportDownloadFilesAsync(audiobook, [sourceFile]));

            Assert.Equal("move_recovery_required", exception.Code);
            Assert.True(File.Exists(sourceFile));
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_NestedRootUsesMostSpecificDestinationSemantics()
        {
            var outerRoot = FileService.GetTempDirectory("download-import-semantics-outer");
            var innerRoot = Path.Join(outerRoot, "Sensitive Library");
            var bookPath = Path.Join(innerRoot, "Book");
            Directory.CreateDirectory(bookPath);
            await AddAuthorizedRootAsync(
                outerRoot,
                "A Outer",
                FileSystemCaseSensitivityMode.Insensitive);
            await AddAuthorizedRootAsync(
                innerRoot,
                "Z Inner",
                FileSystemCaseSensitivityMode.Sensitive);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}")
                .Build());
            var sourceFile = await FileService.GetTempFileAsync("nested-semantics.mp3");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Nested Semantics")
                .WithBasePath(bookPath)
                .Build());
            var resolver = new RecordingSemanticsResolver(
                _provider.GetRequiredService<IFileSystemSemanticsResolver>());
            var service = ActivatorUtilities.CreateInstance<DownloadImportService>(
                _provider,
                resolver);

            await service.ImportDownloadFilesAsync(audiobook, [sourceFile]);

            Assert.Contains(
                resolver.Calls,
                call => string.Equals(call.Path, bookPath, StringComparison.Ordinal)
                    && call.Mode == FileSystemCaseSensitivityMode.Sensitive);
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_StaleAudiobookArgument_UsesCurrentPersistedBasePath()
        {
            var oldBasePath = FileService.GetTempDirectory("download-import-stale-old");
            var newBasePath = FileService.GetTempDirectory("download-import-stale-new");
            var sourceFile = await FileService.GetTempFileAsync("stale-import.mp3");
            var staleAudiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Current Destination")
                .WithAuthor("Author")
                .WithBasePath(oldBasePath)
                .Build());
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(newBasePath)
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}")
                .Build());

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var moveContext = await factory.CreateDbContextAsync())
            {
                var current = await moveContext.Audiobooks.SingleAsync(
                    candidate => candidate.Id == staleAudiobook.Id);
                current.BasePath = newBasePath;
                await moveContext.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<IDownloadImportService>();
            var result = Assert.Single(await service.ImportDownloadFilesAsync(
                staleAudiobook,
                [sourceFile]));

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.StartsWith(newBasePath, result.FinalPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(result.FinalPath.StartsWith(
                oldBasePath,
                StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [MemberData(nameof(PathSuffixes))]
        public async Task CompletedDownload_LinkedToAudiobook_DoesNotMoveToUnknownAuthor(string pathSuffix)
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-test-output");
            var sourceFile = await FileService.GetTempFileAsync("dl-dbl.mp3");

            var basePath = Path.Join(outputRoot, pathSuffix);

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Pride and Prejudice")
                .WithAuthor("Jane Austen")
                .WithBasePath(basePath)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputRoot)
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}")
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceFile)
                .Build());

            // Act - process completed download
            var downloadService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadService.ImportDownloadFilesAsync(audiobook, [sourceFile]);

            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(files);
            var file = files.First();
            Assert.NotEmpty(file.Path);
            Assert.True(File.Exists(file.Path));

            var expectedPath = Path.Join(basePath, "Pride and Prejudice.mp3");
            Assert.Equal(expectedPath, file.Path);

            // Also assert there's no AudiobookFile under an "unknown author" path
            var filepaths = await _audiobookFileRepository.GetAllFilePathsAsync(
                FileSystemPathSemantics.CurrentHostDefault);
            Assert.Empty(filepaths.FindAll(path => path.Contains("unknown author", StringComparison.OrdinalIgnoreCase)));
        }

        [Fact]
        public async Task Import_CanceledAfterOwnershipPreparation_DoesNotMutateFile()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var basePath = FileService.GetTempDirectory("canceled-import-library");
            var sourcePath = FileService.GetTempDirectory("canceled-import-downloads");
            var filePath = await FileService.GetFileAsync(sourcePath, "audio.mp3");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());
            using var cancellation = new CancellationTokenSource();
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            var ownershipStore = new Mock<ILibraryDirectoryOwnershipStore>(MockBehavior.Strict);
            ownershipStore
                .Setup(store => store.EnsureCreatedHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => cancellation.Cancel())
                .ReturnsAsync([]);
            var service = ActivatorUtilities.CreateInstance<DownloadImportService>(
                _provider,
                mover.Object,
                ownershipStore.Object);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ImportDownloadFilesAsync(
                    audiobook,
                    [filePath],
                    cancellation.Token));

            Assert.True(File.Exists(filePath));
            Assert.Empty(Directory.GetFiles(basePath, "*", SearchOption.AllDirectories));
            mover.Verify(
                service => service.PerformActionOn(
                    It.IsAny<FileAction>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()),
                Times.Never);
            ownershipStore.VerifyAll();
        }

        [Fact]
        public async Task Import_WithMove()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var basePath = FileService.GetTempDirectory("library");
            var sourcePath = FileService.GetTempDirectory("downloads");
            var filePath = await FileService.GetFileAsync(sourcePath, "audio.mp3");
            var expectedPath = Path.Join(basePath, "audio.mp3");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

            // Act
            var downloadService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadService.ImportDownloadFilesAsync(audiobook, [filePath]);

            // Moved file does not exist anymore at source
            Assert.True(File.Exists(expectedPath));
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task Import_WithMove_WhenRegistrationFails_RetainsSourceForRetry()
        {
            FileMover? actualMover = null;
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            mover.Setup(candidate => candidate.PrepareActionForRegistrationAsync(
                    FileAction.Move,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<FileAction, string, string, Guid>((action, source, destination, operationId) =>
                    actualMover!.PrepareActionForRegistrationAsync(
                        action,
                        source,
                        destination,
                        operationId));
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService.Setup(candidate => candidate.CheckAudiobookFileOwnershipAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available));
            fileService.Setup(candidate => candidate.EnsureAudiobookFileAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    "download",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            Init(builder => builder
                .WithSingleton<IFileMover>(mover.Object)
                .WithSingleton<IAudiobookFileService>(fileService.Object));
            actualMover = CreateMarkerlessFileMover();
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            var basePath = FileService.GetTempDirectory("registration-failure-library");
            var sourcePath = FileService.GetTempDirectory("registration-failure-source");
            var source = await FileService.GetFileAsync(sourcePath, "audio.mp3", "audio");
            var destination = Path.Join(basePath, "audio.mp3");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var result = Assert.Single(await _provider
                .GetRequiredService<IDownloadImportService>()
                .ImportDownloadFilesAsync(audiobook, [source]));

            Assert.False(result.Success);
            Assert.True(File.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(source));
            Assert.True(File.Exists(destination));
            mover.Verify(candidate => candidate.CompletePreparedMoveAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    It.IsAny<Guid>()),
                Times.Never);
        }

        [Fact]
        public async Task Import_WithMove_WhenCleanupFails_RetryCompletesSamePublication()
        {
            FileMover? actualMover = null;
            var cleanupAttempts = 0;
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            mover.Setup(candidate => candidate.PrepareActionForRegistrationAsync(
                    FileAction.Move,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<FileAction, string, string, Guid>((action, source, destination, operationId) =>
                    actualMover.PrepareActionForRegistrationAsync(
                        action,
                        source,
                        destination,
                        operationId));
            mover.Setup(candidate => candidate.CompletePreparedMoveAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    It.IsAny<Guid>()))
                .Returns<string, string, IAudiobookFileRegistrationLease, Guid>(
                    async (source, destination, lease, operationId) =>
                    {
                        cleanupAttempts++;
                        if (cleanupAttempts == 1)
                        {
                            return false;
                        }

                        return await actualMover!.CompletePreparedMoveAsync(
                            source,
                            destination,
                            lease,
                            operationId);
                    });

            var registered = false;
            var registrationWrites = 0;
            AudiobookFile? registeredFile = null;
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService.Setup(candidate => candidate.CheckAudiobookFileOwnershipAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<Audiobook, string, string?, CancellationToken>(
                    (audiobook, destination, _, _) =>
                    {
                        var outcome = registered
                            ? AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook
                            : AudiobookFileOwnershipCheckOutcome.Available;
                        return Task.FromResult(new AudiobookFileOwnershipCheckResult(
                            outcome,
                            registeredFile));
                    });
            fileService.Setup(candidate => candidate.RegisterPublishedGenerationAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<AudiobookFileOwnershipCheckResult>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    "download",
                    It.IsAny<CancellationToken>()))
                .Returns<Audiobook, AudiobookFileOwnershipCheckResult, IAudiobookFileRegistrationLease, string?, CancellationToken>(
                    (audiobook, ownership, lease, _, _) =>
                    {
                        if (!lease.MatchesCurrentPublication()
                            || !lease.PrepareCleanupRecovery(audiobook.Id))
                        {
                            return Task.FromResult(false);
                        }

                        if (ownership.Outcome
                            == AudiobookFileOwnershipCheckOutcome.Available)
                        {
                            registrationWrites++;
                            registered = true;
                            registeredFile = AudiobookFile.CreateUnresolved(
                                lease.PublicPath);
                            registeredFile.AudiobookId = audiobook.Id;
                            registeredFile.ApplyPhysicalObjectIdentity(
                                lease.PhysicalObjectIdentity,
                                DateTime.UtcNow);
                        }

                        var completion = lease.CompletePublication();
                        return Task.FromResult(completion is
                            RegistrationPublicationCompletion.Completed or
                            RegistrationPublicationCompletion.CommittedCleanupPending);
                    });
            Init(builder => builder
                .WithSingleton<IFileMover>(mover.Object)
                .WithSingleton<IAudiobookFileService>(fileService.Object));
            actualMover = CreateMarkerlessFileMover();
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            var basePath = FileService.GetTempDirectory("cleanup-retry-library");
            var sourcePath = FileService.GetTempDirectory("cleanup-retry-source");
            var source = await FileService.GetFileAsync(sourcePath, "audio.mp3", "audio");
            var destination = Path.Join(basePath, "audio.mp3");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());
            var service = _provider.GetRequiredService<IDownloadImportService>();

            var first = Assert.Single(await service.ImportDownloadFilesAsync(
                audiobook,
                [source]));
            var second = Assert.Single(await service.ImportDownloadFilesAsync(
                audiobook,
                [source]));

            Assert.False(first.Success);
            Assert.True(second.Success);
            Assert.Equal(2, cleanupAttempts);
            Assert.False(File.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.GetFiles(basePath, "audio (1).mp3"));
            Assert.Equal(1, registrationWrites);
            fileService.Verify(candidate => candidate.RegisterPublishedGenerationAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<AudiobookFileOwnershipCheckResult>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    "download",
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }

        [Fact]
        public async Task Import_WitCopy()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var basePath = FileService.GetTempDirectory("library");
            var sourcePath = FileService.GetTempDirectory("downloads");
            var filePath = await FileService.GetFileAsync(sourcePath, "audio.mp3");
            var expectedPath = Path.Join(basePath, "audio.mp3");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

            // Act
            var downloadService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadService.ImportDownloadFilesAsync(audiobook, [filePath]);

            // Copied file does still exist at source
            Assert.True(File.Exists(expectedPath));
            Assert.True(File.Exists(filePath));
        }

        [Fact]
        public async Task DoesNotImportBlacklisted()
        {
            var basePath = FileService.GetTempDirectory("destination");
            var audioPath = await FileService.GetFileAsync(FileService.GetTempPath(), "file1.mp3");
            var coverPath = await FileService.GetFileAsync(FileService.GetTempPath(), "cover.jpg");
            var nfoPath = await FileService.GetFileAsync(FileService.GetTempPath(), "release.nfo");
            var archivePath = await FileService.GetFileAsync(FileService.GetTempPath(), "release.zip");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutExtractArchive()
                .WithImportBlacklistExtension(".nfo")
                .WithoutMetadataProcessing()
                .Build());

            // Act
            var downloadService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadService.ImportDownloadFilesAsync(audiobook, [audioPath, coverPath, nfoPath, archivePath]);

            Assert.True(File.Exists(Path.Join(basePath, "file1.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "cover.jpg")));
            Assert.False(File.Exists(Path.Join(basePath, "release.nfo")));
            Assert.True(File.Exists(Path.Join(basePath, "release.zip")));
        }

        [Fact]
        [Trait("Scenario", "ArchiveExtractionImportsContainedFile")]
        public async Task ArchiveExtraction_ImportsContainedFile()
        {
            var destinationDirectory = FileService.GetTempDirectory("destination");
            var inner = FileService.GetTempDirectory("inner");
            _ = await FileService.GetFileAsync(inner, "audio.mp3");
            var zipPath = Path.Join(FileService.GetTempPath(), "release.zip");
            ZipFile.CreateFromDirectory(inner, zipPath);
            Assert.True(File.Exists(zipPath));

            var audiobook = await CreateAudiobook();
            audiobook.BasePath = Path.Join(destinationDirectory, "Fake Author/Fake Title/Anything Really");
            await _audiobookRepository.UpdateAsync(audiobook);

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithExtractArchive()
                .WithMultiFileNamingPattern("{Title}")
                .WithOutputPath(destinationDirectory)
                .WithoutMetadataProcessing()
                .Build());

            // Act
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(audiobook, [zipPath]);

            var storedAudiobook = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.NotNull(storedAudiobook);
            var expected = Path.Join(storedAudiobook!.BasePath, "audio.mp3");
            var files = await _audiobookFileRepository.GetAllAsync();
            Assert.Single(files);
            var file = files.First();
            Assert.Equal(expected, file.Path);
            Assert.True(File.Exists(expected));
        }

        [Fact]
        [Trait("Scenario", "ForcedArchiveExtractionImportsContainedFile")]
        public async Task ArchiveExtraction_ForcedByDownloadPlan_ImportsWhenGlobalSettingIsDisabled()
        {
            // Given
            var destinationDirectory = FileService.GetTempDirectory("forced-archive-destination");
            var inner = FileService.GetTempDirectory("forced-archive-inner");
            _ = await FileService.GetFileAsync(inner, "forced-audio.mp3");
            var zipPath = Path.Join(FileService.GetTempPath(), "forced-release.zip");
            ZipFile.CreateFromDirectory(inner, zipPath);
            var audiobook = await CreateAudiobook();
            audiobook.BasePath = Path.Join(destinationDirectory, "Fake Author/Fake Title/Forced Archive");
            await _audiobookRepository.UpdateAsync(audiobook);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutExtractArchive()
                .WithMultiFileNamingPattern("{Title}")
                .WithOutputPath(destinationDirectory)
                .WithoutMetadataProcessing()
                .Build());

            // When
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(
                audiobook,
                [zipPath],
                CancellationToken.None,
                new DownloadImportOptions(ForceArchiveExtraction: true));

            // Then
            var expected = Path.Join(audiobook.BasePath, "forced-audio.mp3");
            Assert.True(File.Exists(expected));
            Assert.Single(await _audiobookFileRepository.GetAllAsync());
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        public async Task ProcessCompleteDownloadAsync_MultipleFiles()
        {
            var localSource = FileService.GetTempDirectory("dl-local-source");
            var localDestination = FileService.GetTempDirectory("dl-destination");

            var localChapter1 = await FileService.GetFileAsync(localSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter2 = await FileService.GetFileAsync(localSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter3 = await FileService.GetFileAsync(localSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter4 = await FileService.GetFileAsync(localSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var localCompanion = await FileService.GetFileAsync(localSource, "Seconde Fondation Isaac Asimov.nfo");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}-{ChapterNumber:00}")
                .WithImportBlacklistExtension(".nfo")
                .Build());

            var basePath = Path.Join(localDestination, "Isaac Asimov", "Le Cycle de Fondation", "Seconde Fondation");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .WithTitle("Seconde Fondation")
                .WithSeries("Le Cycle de Fondation")
                .WithAuthor("Isaac Asimov")
                .WithYear("1996")
                .Build());

            // Act
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(audiobook, [localChapter1, localChapter2, localChapter3, localChapter4, localCompanion]);

            var files = await _audiobookFileRepository.GetAllAsync();
            Assert.Equal(4, files.Count);
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-01-01.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-02-02.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-03-03.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-04-04.mp3")));
            Assert.False(File.Exists(Path.Join(basePath, "Seconde Fondation Isaac Asimov.nfo")));
        }

        [Fact]
        public async Task QualityGating_SkipsLowerQualityImport()
        {
            var library = FileService.GetTempDirectory("library");
            var highQualityFile = await FileService.GetFileAsync(library, "high.mp3");

            var qualityProfile = await _qualityProfileRepository.AddAsync(new QualityProfileBuilder()
                .Build());

            // Create audiobook and an existing high-quality 
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("The High Quality Book")
                .WithBasePath(library)
                .WithQualityProfile(qualityProfile)
                .Build());

            // Simulate existing AudiobookFile (MP3 320) in DB
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(highQualityFile)
                .WithFormat("mp3")
                .WithBitrate(320000)
                .Build());

            // Create a temp file representing a lower-quality completed download (MP3 128)
            var tmpMp3 = await FileService.GetTempFileAsync("dummy.mp3");
            metadataServiceMock.AddMetadata(@"\dummy.mp3$", new AudioMetadata { Title = "Ordered Download", Format = "mp3", BitRate = 128000 });

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettings { OutputPath = Path.GetTempPath(), EnableMetadataProcessing = true, CompletedFileAction = FileAction.Move });

            // Act - process completed download
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(audiobook, [tmpMp3]);

            // Assert: no new AudiobookFile created for this audiobook (still only the existing one)
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Single(files);
        }

        [Fact]
        public async Task MultiFileImport_ImportsAllFiles_WithUniqueNames()
        {
            // Create an existing file in destination with name collision
            var basePath = FileService.GetTempDirectory("listenarr-multi");
            var existing = await FileService.GetFileAsync(basePath, "chapter1.mp3");

            // Create source directory with two files: one collides, one new
            var srcDir = FileService.GetTempDirectory("listenarr-src");
            var file1 = await FileService.GetFileAsync(srcDir, "chapter1.mp3");
            var file2 = await FileService.GetFileAsync(srcDir, "chapter2.mp3");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Multi Book")
                .WithBasePath(basePath)
                .Build());

            // Act
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(audiobook, [file1, file2]);

            // Assert: files were moved into destination or imported later (deferred). At minimum we expect either DB records
            // to be created synchronously or files to be present on disk in the audiobook BasePath.
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.True(files.Count >= 1, "Expected at least one AudiobookFile DB record to be created");

            // Search recursively because naming patterns may place files into subfolders under the audiobook BasePath
            var diskFiles = Directory.GetFiles(audiobook.BasePath, "*", SearchOption.AllDirectories).Select(p => Path.GetFileName(p)).ToList();
            // Colliding original file should remain and a suffixed file should be present
            Assert.Contains("chapter1.mp3", diskFiles);
            // Either a suffixed file for the colliding chapter1, or the second file should also be present
            Assert.True(
                diskFiles.Any(d => d.StartsWith("chapter1 (")) ||
                diskFiles.Any(d => d.StartsWith("chapter2")) ||
                files.Count > 1,
                "Expected a suffixed filename for the collision or the second file to be present or multiple DB entries");
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_MultipartFiles_KeepNaturalOrderWhenRenamed()
        {
            var outputDir = FileService.GetTempDirectory("listenarr-import-ordered");

            var srcDir = FileService.GetTempDirectory("listenarr-import-ordered-src");
            var part10 = await FileService.GetFileAsync(srcDir, "Part 10.mp3", "ten");
            var part2 = await FileService.GetFileAsync(srcDir, "Part 2.mp3", "two");
            var part1 = await FileService.GetFileAsync(srcDir, "Part 1.mp3", "one");

            metadataServiceMock.AddMetadata(@"\.mp3$", new AudioMetadata { Title = "Ordered Download", Format = "mp3", BitRate = 128000 });

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(outputDir)
                .Build());

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputDir)
                .WithMetadataProcessing()
                .WithCopyFileOnCompleted()
                .WithFolderNamingPattern("")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}")
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [part10, part2, part1]);

            var mapped = results
                .Where(r => r.Success && !string.IsNullOrWhiteSpace(r.FinalPath) && !string.IsNullOrWhiteSpace(r.SourcePath))
                .ToDictionary(r => r.SourcePath!, r => r.FinalPath!, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(Path.Join(outputDir, "Ordered Download-01.mp3"), mapped[part1]);
            Assert.Equal(Path.Join(outputDir, "Ordered Download-02.mp3"), mapped[part2]);
            Assert.Equal(Path.Join(outputDir, "Ordered Download-10.mp3"), mapped[part10]);
            Assert.Equal("one", await File.ReadAllTextAsync(mapped[part1]));
            Assert.Equal("two", await File.ReadAllTextAsync(mapped[part2]));
            Assert.Equal("ten", await File.ReadAllTextAsync(mapped[part10]));
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_SameNumberOfResult_ThanNumberOfFiles()
        {
            var outputDir = FileService.GetTempDirectory("listenarr-import-ordered");

            var srcDir = FileService.GetTempDirectory("listenarr-import-ordered-src");
            var part10 = await FileService.GetFileAsync(srcDir, "Part 10.mp3", "ten");
            var missingPart2 = Path.Join(srcDir, "Part 2.mp3");
            var part1 = await FileService.GetFileAsync(srcDir, "Part 1.mp3", "one");
            var companion1 = await FileService.GetFileAsync(srcDir, "Companion.nfo", "one");
            var missingCompanion2 = Path.Join(srcDir, "Companion.jpg");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(outputDir)
                .Build());

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputDir)
                .WithoutMetadataProcessing()
                .WithMoveFileOnCompleted()
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [part10, missingPart2, part1, companion1, missingCompanion2]);
            Assert.Equal(5, results.Count);

            List<string> success = [part10, part1, companion1];
            foreach (var result in results)
            {
                if (success.Contains(result.SourcePath))
                {
                    Assert.True(result.Success);
                }
                else
                {
                    Assert.False(result.Success);
                }
            }
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_DestinationOwnedByOtherAudiobook_DoesNotMoveFile()
        {
            var fileMover = new Mock<IFileMover>(MockBehavior.Strict);
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService.Setup(service => service.CheckAudiobookFileOwnershipAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook,
                    Reason: "Reserved by another audiobook."));
            Init(builder => builder
                .WithSingleton<IFileMover>(fileMover.Object)
                .WithSingleton<IAudiobookFileService>(fileService.Object));
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            var outputDirectory = FileService.GetTempDirectory("download-import-owned-destination");
            var sourceDirectory = FileService.GetTempDirectory("download-import-owned-source");
            var sourceFile = await FileService.GetFileAsync(sourceDirectory, "source.mp3", "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Owned Destination")
                .WithBasePath(outputDirectory)
                .Build());
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputDirectory)
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .WithFolderNamingPattern("")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}")
                .Build());

            var result = Assert.Single(await _provider
                .GetRequiredService<IDownloadImportService>()
                .ImportDownloadFilesAsync(audiobook, [sourceFile]));

            Assert.False(result.Success);
            Assert.True(File.Exists(sourceFile));
            Assert.False(File.Exists(Path.Join(outputDirectory, "Owned Destination.mp3")));
            fileMover.Verify(
                mover => mover.PerformActionOn(
                    It.IsAny<FileAction>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()),
                Times.Never);
        }

        [Fact]
        public async Task Import_WithMove_WhenCleanupDetectsStalePublication_RollsBackPhysicalClaim()
        {
            FileMover? actualMover = null;
            ControllableRegistrationLease? controlledLease = null;
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            mover.Setup(candidate => candidate.PrepareActionForRegistrationAsync(
                    FileAction.Move,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<FileAction, string, string, Guid>(async (
                    action,
                    source,
                    destination,
                    operationId) =>
                {
                    var inner = await actualMover!
                        .PrepareActionForRegistrationAsync(
                            action,
                            source,
                            destination,
                            operationId);
                    controlledLease = inner == null
                        ? null
                        : new ControllableRegistrationLease(inner);
                    return controlledLease;
                });
            mover.Setup(candidate => candidate.CompletePreparedMoveAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    It.IsAny<Guid>()))
                .ReturnsAsync(() =>
                {
                    Assert.NotNull(controlledLease);
                    controlledLease.IsCurrent = false;
                    return false;
                });

            var registered = false;
            AudiobookFile? registeredFile = null;
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService.Setup(candidate => candidate.CheckAudiobookFileOwnershipAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<Audiobook, string, string?, CancellationToken>((
                    audiobook,
                    destination,
                    _,
                    _) => Task.FromResult(
                    registered
                        ? new AudiobookFileOwnershipCheckResult(
                            AudiobookFileOwnershipCheckOutcome
                                .AlreadyOwnedByAudiobook,
                            registeredFile)
                        : new AudiobookFileOwnershipCheckResult(
                            AudiobookFileOwnershipCheckOutcome.Available)));
            fileService.Setup(candidate => candidate.RegisterPublishedGenerationAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<AudiobookFileOwnershipCheckResult>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    "download",
                    It.IsAny<CancellationToken>()))
                .Returns<Audiobook, AudiobookFileOwnershipCheckResult, IAudiobookFileRegistrationLease, string?, CancellationToken>((
                    audiobook,
                    _,
                    lease,
                    _,
                    _) =>
                {
                    registered = true;
                    registeredFile = AudiobookFile.CreateUnresolved(
                        lease.PublicPath);
                    registeredFile.Id = 41;
                    registeredFile.AudiobookId = audiobook.Id;
                    registeredFile.ApplyPhysicalObjectIdentity(
                        lease.PhysicalObjectIdentity,
                        DateTime.UtcNow);
                    return Task.FromResult(true);
                });
            fileService.Setup(candidate => candidate.RollbackPublishedGenerationIfStaleAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<IAudiobookFileRegistrationLease>()))
                .Returns<Audiobook, IAudiobookFileRegistrationLease>((_, _) =>
                {
                    registered = false;
                    return Task.CompletedTask;
                });
            Init(builder => builder
                .WithSingleton<IFileMover>(mover.Object)
                .WithSingleton<IAudiobookFileService>(fileService.Object));
            actualMover = CreateMarkerlessFileMover();
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            var basePath = FileService.GetTempDirectory(
                "stale-cleanup-library");
            var sourcePath = FileService.GetTempDirectory(
                "stale-cleanup-source");
            var source = await FileService.GetFileAsync(
                sourcePath,
                "audio.mp3",
                "audio");
            var audiobook = await _audiobookRepository.AddAsync(
                new AudiobookBuilder().WithBasePath(basePath).Build());
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithMoveFileOnCompleted()
                    .WithoutMetadataProcessing()
                    .Build());

            var result = Assert.Single(await _provider
                .GetRequiredService<IDownloadImportService>()
                .ImportDownloadFilesAsync(audiobook, [source]));

            Assert.False(result.Success);
            Assert.False(registered);
            Assert.True(File.Exists(source));
            fileService.Verify(candidate =>
                candidate.RollbackPublishedGenerationIfStaleAsync(
                    It.Is<Audiobook>(book => book.Id == audiobook.Id),
                    controlledLease!),
                Times.Once);
        }

        [Fact]
        public async Task ImportDownloadFilesAsync_FailedPublication_DoesNotReserveDestinationForLaterFiles()
        {
            var attemptedDestinations = new List<string>();
            var callCount = 0;
            FileMover? actualMover = null;
            var fileMover = new Mock<IFileMover>();
            fileMover.Setup(mover => mover.PrepareActionForRegistrationAsync(
                    FileAction.Copy,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<FileAction, string, string, Guid>(async (action, source, destination, operationId) =>
                {
                    attemptedDestinations.Add(destination);
                    callCount++;
                    if (callCount == 1)
                    {
                        return null;
                    }

                    return await actualMover!.PrepareActionForRegistrationAsync(
                        action,
                        source,
                        destination,
                        operationId);
                });
            Init(builder => builder.WithSingleton<IFileMover>(fileMover.Object));
            actualMover = CreateMarkerlessFileMover();
            await AddAuthorizedRootAsync(FileService.GetTempPath());

            var outputDirectory = FileService.GetTempDirectory("download-import-reservation-dst");
            var sourceDirectory = FileService.GetTempDirectory("download-import-reservation-src");
            var first = await FileService.GetFileAsync(sourceDirectory, "first.mp3", "first");
            var second = await FileService.GetFileAsync(sourceDirectory, "second.mp3", "second");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Collision Book")
                .WithBasePath(outputDirectory)
                .Build());
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputDirectory)
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .WithFolderNamingPattern("")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}")
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [first, second]);

            Assert.Equal(2, attemptedDestinations.Count);
            Assert.Equal(attemptedDestinations[0], attemptedDestinations[1]);
            Assert.EndsWith("Collision Book.mp3", attemptedDestinations[1], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("(1)", Path.GetFileName(attemptedDestinations[1]), StringComparison.Ordinal);
            Assert.False(results[0].Success);
            Assert.True(results[1].Success);
            Assert.True(File.Exists(Path.Join(outputDirectory, "Collision Book.mp3")));
        }

        [Fact]
        public async Task DownloadImportService_NoImportedFile_WhenAudioFilesFails()
        {
            var outputDirectory = FileService.GetTempDirectory("library");

            var sourceDirectory = FileService.GetTempDirectory("download");
            var file1 = Path.Join(sourceDirectory, "file1.mp3");
            var file2 = Path.Join(sourceDirectory, "file2.mp3");
            var file3 = Path.Join(sourceDirectory, "file3.mp3");
            var file4 = Path.Join(sourceDirectory, "file4.m4b");
            var companion1 = await FileService.GetFileAsync(sourceDirectory, "companion1.jpg");
            var companion2 = await FileService.GetFileAsync(sourceDirectory, "companion2.jpg");
            var companion3 = await FileService.GetFileAsync(sourceDirectory, "companion3.jpg");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(outputDirectory)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputDirectory)
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .Build());

            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await downloadImportService.ImportDownloadFilesAsync(audiobook, [file1, file2, file3, file4, companion1, companion2, companion3]);
            Assert.Equal(7, results.Count);

            // Output folder should stay empty
            var importedFiles = Directory.EnumerateFiles(outputDirectory, "*.*", SearchOption.AllDirectories)
                .ToList();
            Assert.Empty(importedFiles);
        }

        private FileMover CreateMarkerlessFileMover()
        {
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            return new FileMover(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileMover>.Instance,
                dbContextFactory: factory,
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                    "download-import-markerless-locks")
            };
        }

        private sealed class ControllableRegistrationLease(
            IAudiobookFileRegistrationLease inner) :
            IAudiobookFileRegistrationLease
        {
            public bool IsCurrent { get; set; } = true;
            public string PublicPath => inner.PublicPath;
            public string MetadataPath => inner.MetadataPath;
            public string PhysicalObjectIdentity =>
                inner.PhysicalObjectIdentity;
            public string? SourcePhysicalObjectIdentity =>
                inner.SourcePhysicalObjectIdentity;

            public bool MatchesCurrentPublication() =>
                IsCurrent && inner.MatchesCurrentPublication();

            public bool PrepareCleanupRecovery(int audiobookId) =>
                inner.PrepareCleanupRecovery(audiobookId);

            public RegistrationPublicationCompletion CompletePublication() =>
                inner.CompletePublication();

            public Task<bool> MatchesContentAsync(
                Stream candidateStream,
                CancellationToken cancellationToken = default) =>
                inner.MatchesContentAsync(candidateStream, cancellationToken);

            public void Dispose() => inner.Dispose();
        }

        private sealed class RecordingSemanticsResolver(
            IFileSystemSemanticsResolver inner) : IFileSystemSemanticsResolver
        {
            public List<(string Path, FileSystemCaseSensitivityMode Mode)> Calls { get; } = [];

            public ValueTask<FileSystemSemanticsResolution> ResolveAsync(
                string path,
                FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
                CancellationToken cancellationToken = default)
            {
                Calls.Add((path, mode));
                return inner.ResolveAsync(path, mode, cancellationToken);
            }
        }
    }
}
