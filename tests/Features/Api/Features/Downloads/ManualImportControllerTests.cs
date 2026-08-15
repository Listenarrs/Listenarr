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
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Api.Features.Downloads
{
    public class ManualImport_MultiFileCollisionTests : IDisposable
    {

        private readonly AudiobookOperationCoordinator _operationCoordinator = new();
        private List<string> _tempDirectories = [];

        public void Dispose()
        {
            foreach (var directory in _tempDirectories)
            {
                TryDeleteDirectory(directory);
            }

            _tempDirectories.Clear();
            _operationCoordinator.Dispose();
        }
        private static void TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        private String CreateTempDirectory(string name)
        {
            var directory = Path.Join(Path.GetTempPath(), name, Guid.NewGuid().ToString());
            Directory.CreateDirectory(directory);

            _tempDirectories.Add(directory);

            return directory;
        }

        [WindowsFact]
        public async Task GeneratePathAsync_ForeignConfiguredOutputAlias_DoesNotReclassifyCustomBasePath()
        {
            var broadRoot = WindowsPathTestFixture
                .CreateRootRelativeAliasCompatibleDirectory(
                    "manual-import-foreign-output-root");
            _tempDirectories.Add(broadRoot);
            var customBasePath = Path.Join(broadRoot, "Custom Book Folder");
            var foreignOutputPath = WindowsPathTestFixture
                .GetRootRelativeForeignAlias(customBasePath);

            var settings = new ApplicationSettings
            {
                OutputPath = foreignOutputPath,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };
            var audiobook = new Audiobook
            {
                Title = "Book",
                Authors = ["Author"],
                BasePath = customBasePath
            };
            var metadata = audiobook.CreateBasicAudioMetadata();
            var item = new ManualImportItemDto
            {
                FullPath = Path.Join(broadRoot, "incoming.m4b"),
                MatchedAudiobookId = 1
            };
            var planner = new ManualImportPathPlanner(new FileNamingService(
                Mock.Of<IConfigurationService>(),
                NullLogger<FileNamingService>.Instance));

            var plan = await planner.GeneratePathAsync(
                audiobook,
                metadata,
                item,
                customBasePath,
                [new RootFolder { Id = 1, Name = "Library", Path = broadRoot }],
                settings,
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Windows,
                    FileSystemCaseSensitivity.Insensitive));

            Assert.Equal(Path.Join(customBasePath, "Book.m4b"), plan.DestinationPath);
            Assert.Equal(customBasePath, plan.AudiobookBasePath);
        }

        private FileMover CreateMarkerlessFileMover()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase($"manual-import-{Guid.NewGuid():N}")
                .Options;
            return new FileMover(
                Mock.Of<Microsoft.Extensions.Logging.ILogger<FileMover>>(),
                dbContextFactory: new ManualImportDbContextFactory(options),
                semanticsResolver: new FileSystemSemanticsResolver())
            {
                FileMoveLockDirectoryForTest = CreateTempDirectory(
                    "listenarr-manual-file-move-locks")
            };
        }

        private sealed class ManualImportDbContextFactory(
            DbContextOptions<ListenArrDbContext> options) :
            IDbContextFactory<ListenArrDbContext>
        {
            public ListenArrDbContext CreateDbContext() => new(options);

            public Task<ListenArrDbContext> CreateDbContextAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateDbContext());
        }

        public static Mock<IAudiobookRepository> GetRepoMock(Audiobook book)
        {
            var repoMock = new Mock<IAudiobookRepository>();
            repoMock.Setup(r => r.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => id == book.Id ? book : null);
            repoMock.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            return repoMock;
        }

        public static Mock<IScanQueueService> GetScanMock()
        {
            var scanMock = new Mock<IScanQueueService>();
            scanMock.Setup(service => service.EnqueueScanAsync(
                    It.IsAny<ScanEnqueueCommand>()))
                .ReturnsAsync(Guid.NewGuid());

            return scanMock;
        }

        public ManualImportController GetController(
            Audiobook book,
            ApplicationSettings settings,
            Mock<IAudiobookRepository> repoMock = null,
            Mock<IScanQueueService> scanMock = null,
            IFileMover fileMover = null,
            IAudiobookFileService audiobookFileService = null,
            IReadOnlyList<RootFolder> rootFolders = null,
            IFileSystemSemanticsResolver semanticsResolver = null,
            IFilesystemMutationCoordinator filesystemMutationCoordinator = null,
            ILibraryDirectoryOwnershipStore directoryOwnershipStore = null,
            Mock<IMetadataService> metadataMock = null,
            IMoveQueueService moveQueueServiceOverride = null,
            ILibraryFilesystemMutationGate filesystemMutationGate = null)
        {
            repoMock ??= GetRepoMock(book);
            scanMock ??= GetScanMock();
            fileMover ??= CreateMarkerlessFileMover();
            if (audiobookFileService == null)
            {
                var audiobookFileServiceMock = new Mock<IAudiobookFileService>();
                audiobookFileServiceMock
                    .Setup(service => service.CheckAudiobookFileOwnershipAsync(
                        It.IsAny<Audiobook>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                        AudiobookFileOwnershipCheckOutcome.Available));
                audiobookFileServiceMock
                    .Setup(service => service.EnsureAudiobookFileAsync(
                        It.IsAny<Audiobook>(),
                        It.IsAny<IAudiobookFileRegistrationLease>(),
                        It.IsAny<string?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);
                audiobookFileServiceMock
                    .Setup(service => service.RegisterPublishedGenerationAsync(
                        It.IsAny<Audiobook>(),
                        It.IsAny<AudiobookFileOwnershipCheckResult>(),
                        It.IsAny<IAudiobookFileRegistrationLease>(),
                        It.IsAny<string?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync((
                        Audiobook audiobook,
                        AudiobookFileOwnershipCheckResult _,
                        IAudiobookFileRegistrationLease registrationLease,
                        string? _,
                        CancellationToken _) =>
                    {
                        if (!registrationLease.MatchesCurrentPublication()
                            || !registrationLease.PrepareCleanupRecovery(audiobook.Id))
                        {
                            return false;
                        }

                        return registrationLease.CompletePublication() is
                            RegistrationPublicationCompletion.Completed or
                            RegistrationPublicationCompletion.CommittedCleanupPending;
                    });
                audiobookFileServiceMock
                    .Setup(service => service.RegisterPublishedGenerationWithBasePathAsync(
                        It.IsAny<Audiobook>(),
                        It.IsAny<AudiobookFileOwnershipCheckResult>(),
                        It.IsAny<IAudiobookFileRegistrationLease>(),
                        It.IsAny<string>(),
                        It.IsAny<string?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync((
                        Audiobook audiobook,
                        AudiobookFileOwnershipCheckResult _,
                        IAudiobookFileRegistrationLease registrationLease,
                        string authoritativeBasePath,
                        string? _,
                        CancellationToken _) =>
                    {
                        if (!registrationLease.MatchesCurrentPublication()
                            || !registrationLease.PrepareCleanupRecovery(audiobook.Id))
                        {
                            return false;
                        }

                        audiobook.BasePath = authoritativeBasePath;
                        return registrationLease.CompletePublication() is
                            RegistrationPublicationCompletion.Completed or
                            RegistrationPublicationCompletion.CommittedCleanupPending;
                    });
                audiobookFileServiceMock
                    .Setup(service => service.RefreshPhysicalGenerationAsync(
                        It.IsAny<Audiobook>(),
                        It.IsAny<int>(),
                        It.IsAny<string?>(),
                        It.IsAny<IAudiobookFileRegistrationLease>(),
                        It.IsAny<string?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);
                audiobookFileService = audiobookFileServiceMock.Object;
            }

            metadataMock ??= new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "Ordered Book", Format = "mp3", BitRate = 128000 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("(Foreword by Joe Haldeman).mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Chapter 01.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Chapter 02.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 2 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Disc 1.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", DiscNumber = 1, TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Disc 2.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", DiscNumber = 2, TrackNumber = 2 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Companion Book.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Companion Book", Album = "Companion Book", Artist = "Author A", Format = "mp3" });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Different Book.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Different Book", Album = "Different Book", Artist = "Author A", Format = "mp3" });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Track 01.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Companion Book", Format = "mp3", BitRate = 128000 });
            metadataMock.Setup(m => m.WriteAsinTagAsync(
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var rootFolderMock = new Mock<IRootFolderService>();
            rootFolderMock.Setup(r => r.GetAllAsync()).ReturnsAsync(
                rootFolders?.ToList() ?? []);
            semanticsResolver ??= new FileSystemSemanticsResolver();
            var scanAuthorizationMock = new Mock<IScanPathAuthorizationService>();
            scanAuthorizationMock
                .Setup(service => service.AuthorizeAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((path, _) =>
                {
                    var fullPath = Path.GetFullPath(path);
                    var configuredBoundary = !string.IsNullOrWhiteSpace(settings.OutputPath)
                        ? Path.GetFullPath(settings.OutputPath)
                        : fullPath;
                    if (!FileSystemPathIdentity.IsSameOrInside(
                            fullPath,
                            configuredBoundary,
                            FileSystemPathSemantics.CurrentHostDefault))
                    {
                        configuredBoundary = fullPath;
                    }

                    var identity = PathIdentitySnapshot.FromResolution(
                        FileSystemPathSemantics.CurrentHostDefault,
                        FileSystemCaseSensitivityMode.Auto,
                        configuredBoundary,
                        fullPath);
                    return Task.FromResult(
                        ScanPathAuthorizationResult.Authorized(
                            fullPath,
                            identity,
                            new ScanPathPhysicalIdentity(
                                $"boundary:{configuredBoundary}",
                                $"scan:{fullPath}")));
                });
            if (directoryOwnershipStore == null)
            {
                var directoryOwnershipStoreMock = new Mock<ILibraryDirectoryOwnershipStore>();
                directoryOwnershipStoreMock
                    .Setup(store => store.EnsureCreatedHierarchyAsync(
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<FileSystemPathSemantics>(),
                        It.IsAny<string>(),
                        It.IsAny<Guid?>(),
                        It.IsAny<int?>(),
                        It.IsAny<CancellationToken>()))
                    .Callback<string, string, FileSystemPathSemantics, string, Guid?, int?, CancellationToken>(
                        (destinationDirectory, _, _, _, _, _, _) =>
                            Directory.CreateDirectory(destinationDirectory))
                    .ReturnsAsync([]);
                directoryOwnershipStore = directoryOwnershipStoreMock.Object;
            }

            var moveQueueService = new Mock<IMoveQueueService>();
            moveQueueService.Setup(service => service.EnsureFilesystemMutationAllowedAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            return new ManualImportController(
                Mock.Of<Microsoft.Extensions.Logging.ILogger<ManualImportController>>(),
                repoMock.Object,
                metadataMock.Object,
                new FileNamingService(configMock.Object, NullLogger<FileNamingService>.Instance),
                configMock.Object,
                scanMock.Object,
                scanAuthorizationMock.Object,
                rootFolderMock.Object,
                fileMover,
                audiobookFileService,
                new LocalFileSystem(),
                semanticsResolver,
                filesystemMutationCoordinator ?? new FilesystemMutationCoordinator(),
                _operationCoordinator,
                moveQueueServiceOverride ?? moveQueueService.Object,
                directoryOwnershipStore,
                filesystemMutationGate ?? TestLibraryFilesystemReadiness.Ready()
            );
        }

        [Fact]
        public async Task Start_FilesystemInitializing_BlocksBeforeAnyImportMutation()
        {
            var basePath = CreateTempDirectory("listenarr-manual-initializing-destination");
            var sourceDirectory = CreateTempDirectory("listenarr-manual-initializing-source");
            var sourceFile = Path.Join(sourceDirectory, "chapter.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio");
            var book = new Audiobook
            {
                Id = 39,
                Title = "Initializing Manual Import",
                BasePath = basePath
            };
            var fileMover = new Mock<IFileMover>(MockBehavior.Strict);
            var readiness = new TestLibraryFilesystemReadiness();
            readiness.SetRunning("AudiobookFileIdentities");
            var controller = GetController(
                book,
                new ApplicationSettings { OutputPath = basePath },
                fileMover: fileMover.Object,
                filesystemMutationGate: readiness);
            var request = new ManualImportRequestDto
            {
                Path = sourceDirectory,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = sourceFile,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var exception = await Assert.ThrowsAsync<ApplicationUnavailableException>(async () =>
                await controller.Start(request));

            Assert.Equal("filesystem_initializing", exception.Code);
            Assert.True(File.Exists(sourceFile));
            fileMover.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Start_UnresolvedMoveExecution_BlocksBeforeFilesystemImport()
        {
            var basePath = CreateTempDirectory("listenarr-manual-unresolved-destination");
            var sourceDirectory = CreateTempDirectory("listenarr-manual-unresolved-source");
            var sourceFile = Path.Join(sourceDirectory, "chapter.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio");
            var book = new Audiobook
            {
                Id = 40,
                Title = "Unresolved Manual Import",
                BasePath = basePath
            };
            var moveQueue = new Mock<IMoveQueueService>(MockBehavior.Strict);
            moveQueue.Setup(service => service.EnsureFilesystemMutationAllowedAsync(
                    book.Id,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ApplicationConflictException(
                    "move_recovery_required",
                    "An interrupted move still owns this audiobook's filesystem state."));
            var fileMover = new Mock<IFileMover>(MockBehavior.Strict);
            var controller = GetController(
                book,
                new ApplicationSettings { OutputPath = basePath },
                fileMover: fileMover.Object,
                moveQueueServiceOverride: moveQueue.Object);
            var request = new ManualImportRequestDto
            {
                Path = sourceDirectory,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = sourceFile,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var result = await controller.Start(request);

            var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result.Result);
            var payload = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
            Assert.Contains("move_recovery_required", payload, StringComparison.Ordinal);
            Assert.True(File.Exists(sourceFile));
            fileMover.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task Start_CanceledWhileWaitingForFilesystemMutation_DoesNotImport()
        {
            var basePath = CreateTempDirectory("listenarr-manual-canceled-destination");
            var sourceDirectory = CreateTempDirectory("listenarr-manual-canceled-source");
            var sourceFile = Path.Join(sourceDirectory, "chapter.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio");
            var book = new Audiobook
            {
                Id = 41,
                Title = "Canceled Manual Import",
                BasePath = basePath
            };
            var mutationCoordinator = new FilesystemMutationCoordinator();
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = mutationCoordinator.ExecuteExclusiveAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
            });
            await entered.Task;
            var controller = GetController(
                book,
                new ApplicationSettings { OutputPath = basePath },
                filesystemMutationCoordinator: mutationCoordinator);
            var request = new ManualImportRequestDto
            {
                Path = sourceDirectory,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = sourceFile,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };
            using var cancellation = new CancellationTokenSource();
            var import = controller.Start(request, cancellation.Token);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);

            Assert.Empty(Directory.EnumerateFileSystemEntries(basePath));
            Assert.True(File.Exists(sourceFile));
            release.SetResult();
            await holder;
        }

        [Fact]
        public async Task Start_CanceledAfterOwnershipPreparation_DoesNotMutateFile()
        {
            var basePath = CreateTempDirectory("listenarr-manual-canceled-after-ownership-destination");
            var sourceDirectory = CreateTempDirectory("listenarr-manual-canceled-after-ownership-source");
            var sourceFile = Path.Join(sourceDirectory, "chapter.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio");
            var book = new Audiobook
            {
                Id = 45,
                Title = "Canceled Before File Mutation",
                BasePath = basePath
            };
            using var cancellation = new CancellationTokenSource();
            var directoryOwnershipStore = new Mock<ILibraryDirectoryOwnershipStore>(MockBehavior.Strict);
            directoryOwnershipStore
                .Setup(store => store.EnsureCreatedHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => cancellation.Cancel())
                .ReturnsAsync(Array.Empty<LibraryDirectoryOwnership>());
            var fileMover = new Mock<IFileMover>(MockBehavior.Strict);
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = basePath,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}"
                },
                fileMover: fileMover.Object,
                directoryOwnershipStore: directoryOwnershipStore.Object);
            var request = new ManualImportRequestDto
            {
                Path = sourceDirectory,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = sourceFile,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                controller.Start(request, cancellation.Token));

            Assert.True(File.Exists(sourceFile));
            Assert.Empty(Directory.GetFiles(basePath, "*", SearchOption.AllDirectories));
            fileMover.Verify(
                mover => mover.PerformActionOn(
                    It.IsAny<FileAction>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()),
                Times.Never);
            directoryOwnershipStore.VerifyAll();
        }

        [Fact]
        public async Task InteractiveManualImport_MultipleFiles_ResolvesCollisionsWithinBatch()
        {
            var basePath = CreateTempDirectory("listenarr-manual-batch");
            var srcDir = CreateTempDirectory("listenarr-manual-src");

            var book = new Audiobook { Id = 42, Title = "Batch Book", BasePath = basePath };

            // Create two source files
            var src1 = Path.Join(srcDir, "one.mp3");
            var src2 = Path.Join(srcDir, "two.mp3");
            await File.WriteAllTextAsync(src1, "one");
            await File.WriteAllTextAsync(src2, "two");

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto { FullPath = src1, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = src2, MatchedAudiobookId = book.Id }
                ]
            };

            var controller = GetController(book, new ApplicationSettings { OutputPath = basePath });

            await controller.Start(request);

            // Assert: both files should exist in the audiobook base path, second should have a suffix if name collided
            var diskFiles = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories).Select(p => Path.GetFileName(p)).ToList();

            Assert.Contains(diskFiles, f => f.Equals("Batch Book.mp3", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Batch Book"));
            // Expect at least two files (the second should be suffixed)
            Assert.True(diskFiles.Count >= 2, "Expected at least two files in destination (one suffixed for the collision)");
        }

        [Fact]
        public async Task InteractiveManualImport_MissingBasePath_UsesConfiguredDestinationRoot()
        {
            var outputRoot = CreateTempDirectory("listenarr-manual-managed-destination");
            var sourceRoot = CreateTempDirectory("listenarr-manual-unmanaged-source");
            var sourceFile = Path.Join(sourceRoot, "chapter.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio");
            var book = new Audiobook
            {
                Id = 421,
                Title = "Managed Destination",
                Authors = ["Author"]
            };
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = outputRoot,
                    FolderNamingPattern = "{Author}/{Title}",
                    FileNamingPattern = "{Title}"
                });
            var request = new ManualImportRequestDto
            {
                Path = sourceRoot,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = sourceFile,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var result = await controller.Start(request);

            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
            var importedFiles = Directory.GetFiles(
                outputRoot,
                "*.mp3",
                SearchOption.AllDirectories);
            Assert.Single(importedFiles);
            Assert.True(FileUtils.IsPathSameOrInside(importedFiles[0], outputRoot));
            Assert.False(FileUtils.IsPathSameOrInside(importedFiles[0], sourceRoot));
            Assert.NotNull(book.BasePath);
            Assert.True(FileUtils.IsPathSameOrInside(book.BasePath, outputRoot));
        }

        [Fact]
        public async Task InteractiveManualImport_ExistingBasePathOutsideConfiguredRoots_IsRejected()
        {
            var outputRoot = CreateTempDirectory("listenarr-manual-managed-root");
            var outsideBasePath = CreateTempDirectory("listenarr-manual-unmanaged-destination");
            var sourceRoot = CreateTempDirectory("listenarr-manual-unmanaged-source-existing");
            var sourceFile = Path.Join(sourceRoot, "chapter.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio");
            var book = new Audiobook
            {
                Id = 422,
                Title = "Unmanaged Destination",
                BasePath = outsideBasePath
            };
            var fileMover = new Mock<IFileMover>(MockBehavior.Strict);
            var controller = GetController(
                book,
                new ApplicationSettings { OutputPath = outputRoot },
                fileMover: fileMover.Object);
            var request = new ManualImportRequestDto
            {
                Path = sourceRoot,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = sourceFile,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var result = await controller.Start(request);

            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
            Assert.Empty(Directory.GetFiles(
                outsideBasePath,
                "*",
                SearchOption.AllDirectories));
            Assert.Equal(outsideBasePath, book.BasePath);
            fileMover.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task InteractiveManualImport_NestedRootUsesMostSpecificDestinationSemantics()
        {
            var outerRoot = CreateTempDirectory("listenarr-manual-semantics-outer");
            var innerRoot = Path.Join(outerRoot, "Sensitive Library");
            var bookPath = Path.Join(innerRoot, "Book");
            Directory.CreateDirectory(bookPath);
            var sourceDir = CreateTempDirectory("listenarr-manual-semantics-source");
            var sourceFile = Path.Join(sourceDir, "chapter.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio");
            var roots = new List<RootFolder>
            {
                new()
                {
                    Id = 1,
                    Name = "A Outer",
                    Path = outerRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                },
                new()
                {
                    Id = 2,
                    Name = "Z Inner",
                    Path = innerRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
                }
            };
            var resolver = new RecordingSemanticsResolver(new FileSystemSemanticsResolver());
            var book = new Audiobook
            {
                Id = 44,
                Title = "Semantics Book",
                BasePath = bookPath
            };
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = outerRoot,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}"
                },
                rootFolders: roots,
                semanticsResolver: resolver);
            var request = new ManualImportRequestDto
            {
                Path = sourceDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = sourceFile,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            await controller.Start(request);

            Assert.Contains(
                resolver.Calls,
                call => string.Equals(call.Path, bookPath, StringComparison.Ordinal)
                    && call.Mode == FileSystemCaseSensitivityMode.Sensitive);
        }

        [Fact]
        public async Task InteractiveManualImport_OwnershipConflict_DoesNotMutateFilesystem()
        {
            var destinationRoot = CreateTempDirectory("listenarr-manual-owner-conflict-dest");
            var sourceDir = CreateTempDirectory("listenarr-manual-owner-conflict-src");
            var sourceFile = Path.Join(sourceDir, "chapter.mp3");
            await File.WriteAllTextAsync(sourceFile, "source");

            var book = new Audiobook
            {
                Id = 43,
                Title = "Owned Destination",
                BasePath = destinationRoot
            };
            var fileMover = new Mock<IFileMover>(MockBehavior.Strict);
            var audiobookFileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            audiobookFileService
                .Setup(service => service.CheckAudiobookFileOwnershipAsync(
                    book,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook,
                    Reason: "C:\\private\\manual-import-ownership-secret"));

            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = destinationRoot,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}"
                },
                fileMover: fileMover.Object,
                audiobookFileService: audiobookFileService.Object);
            var request = new ManualImportRequestDto
            {
                Path = sourceDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = sourceFile,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var action = await controller.Start(request);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);
            var responseJson = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            Assert.Contains(
                "The destination file is owned by another audiobook.",
                responseJson,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "manual-import-ownership-secret",
                responseJson,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.Empty(Directory.GetFiles(destinationRoot, "*", SearchOption.AllDirectories));
            fileMover.Verify(
                mover => mover.PerformActionOn(
                    It.IsAny<FileAction>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()),
                Times.Never);
            audiobookFileService.VerifyAll();
        }

        [Fact]
        public async Task InteractiveManualImport_MultipartFiles_UsesStableNaturalOrderAndNumbering()
        {
            var basePath = CreateTempDirectory("listenarr-manual-ordered");

            var book = new Audiobook { Id = 84, Title = "Ordered Book", BasePath = basePath };

            var srcDir = CreateTempDirectory("listenarr-manual-ordered-src");
            var part10 = Path.Join(srcDir, "Part 10.mp3");
            var part2 = Path.Join(srcDir, "Part 2.mp3");
            var part1 = Path.Join(srcDir, "Part 1.mp3");
            await File.WriteAllTextAsync(part10, "ten");
            await File.WriteAllTextAsync(part2, "two");
            await File.WriteAllTextAsync(part1, "one");

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = part10, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = part2, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = part1, MatchedAudiobookId = book.Id }
                }
            };

            var controller = GetController(book, new ApplicationSettings
            {
                OutputPath = basePath,
                FolderNamingPattern = "{Author}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            var diskFiles = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();

            Assert.Contains("Ordered Book-01.mp3", diskFiles);
            Assert.Contains("Ordered Book-02.mp3", diskFiles);
            Assert.Contains("Ordered Book-10.mp3", diskFiles);
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Join(basePath, "Ordered Book-01.mp3")));
            Assert.Equal("two", await File.ReadAllTextAsync(Path.Join(basePath, "Ordered Book-02.mp3")));
            Assert.Equal("ten", await File.ReadAllTextAsync(Path.Join(basePath, "Ordered Book-10.mp3")));
        }

        [Fact]
        public async Task InteractiveManualImport_ForewordAndChapterOne_AvoidsDuplicateNumberedNames()
        {
            var basePath = CreateTempDirectory("listenarr-manual-foreword");
            var srcDir = CreateTempDirectory("listenarr-manual-foreword-sr");

            var book = new Audiobook { Id = 126, Title = "Jack of Shadows", BasePath = basePath };

            var foreword = Path.Join(srcDir, "(Foreword by Joe Haldeman).mp3");
            var chapter1 = Path.Join(srcDir, "Chapter 01.mp3");
            var chapter2 = Path.Join(srcDir, "Chapter 02.mp3");
            await File.WriteAllTextAsync(foreword, "foreword");
            await File.WriteAllTextAsync(chapter1, "chapter1");
            await File.WriteAllTextAsync(chapter2, "chapter2");

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = foreword, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = chapter1, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = chapter2, MatchedAudiobookId = book.Id }
                }
            };

            var controller = GetController(book, new ApplicationSettings
            {
                OutputPath = basePath,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            });

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            var diskFiles = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();

            Assert.Contains("Jack of Shadows-01.mp3", diskFiles);
            Assert.Contains("Jack of Shadows-02.mp3", diskFiles);
            Assert.Contains("Jack of Shadows-03.mp3", diskFiles);
            Assert.DoesNotContain("Jack of Shadows-01 (1).mp3", diskFiles, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task InteractiveManualImport_MultiFileBatch_EnqueuesSingleCommonDirectoryScan()
        {
            var outputRoot = CreateTempDirectory("listenarr-manual-scan-root");
            var srcDir = CreateTempDirectory("listenarr-manual-scan-src");

            var book = new Audiobook { Id = 222, Title = "Jack of Shadows", Authors = new System.Collections.Generic.List<string> { "Roger Zelazny" }, BasePath = outputRoot };

            var disc1 = Path.Join(srcDir, "Disc 1.mp3");
            var disc2 = Path.Join(srcDir, "Disc 2.mp3");
            await File.WriteAllTextAsync(disc1, "disc1");
            await File.WriteAllTextAsync(disc2, "disc2");

            var repoMock = GetRepoMock(book);

            var expectedScanPath = Path.Join(outputRoot, "Roger Zelazny", "Jack of Shadows");
            var scanMock = GetScanMock();

            var controller = GetController(book, new ApplicationSettings
            {
                OutputPath = outputRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "Disc {DiskNumber:00}/{Title}-{DiskNumber:00}"
            }, repoMock, scanMock);

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = disc1, MatchedAudiobookId = book.Id },
                    new ManualImportItemDto { FullPath = disc2, MatchedAudiobookId = book.Id }
                }
            };

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            Assert.Equal(expectedScanPath, book.BasePath);
            scanMock.Verify(service => service.EnqueueScanAsync(
                It.Is<ScanEnqueueCommand>(command =>
                    command.Audiobook.Id == book.Id
                    && command.Path == expectedScanPath
                    && command.PathIdentity.HasValue
                    && command.PhysicalIdentity.HasValue
                    && !command.IsAuthoritativeScope)), Times.Once);
            repoMock.Verify(
                repository => repository.UpdateAsync(It.IsAny<Audiobook>()),
                Times.Never);
        }

        [Fact]
        public async Task InteractiveManualImport_MoveWithCompanionFiles_ImportsSidecarsAndDeletesSourceFolder()
        {
            var destinationRoot = CreateTempDirectory("listenarr-manual-companion-dest");
            var sourceDir = CreateTempDirectory("listenarr-manual-companion-src");

            var book = new Audiobook { Id = 333, Title = "Companion Book", BasePath = destinationRoot };

            var audioFile = Path.Join(sourceDir, "Track 01.mp3");
            var coverFile = Path.Join(sourceDir, "cover.jpg");
            var notesFile = Path.Join(sourceDir, "notes.txt");
            await File.WriteAllTextAsync(audioFile, "audio");
            await File.WriteAllTextAsync(coverFile, "cover");
            await File.WriteAllTextAsync(notesFile, "notes");

            var controller = GetController(book, new ApplicationSettings
            {
                OutputPath = destinationRoot,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                ImportBlacklistExtensions = new System.Collections.Generic.List<string>()
            });

            var request = new ManualImportRequestDto
            {
                Path = sourceDir,
                Mode = "interactive",
                Action = FileAction.Move,
                IncludeCompanionFiles = true,
                CleanupEmptySourceFolders = true,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = audioFile, MatchedAudiobookId = book.Id }
                }
            };

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            Assert.True(File.Exists(Path.Join(destinationRoot, "Companion Book.mp3")));
            Assert.True(File.Exists(Path.Join(destinationRoot, "cover.jpg")));
            Assert.True(File.Exists(Path.Join(destinationRoot, "notes.txt")));
            Assert.False(Directory.Exists(sourceDir));
        }

        [Fact]
        public async Task InteractiveManualImport_FocusedScanFailureAfterCopy_RemainsSuccessful()
        {
            var destinationRoot = CreateTempDirectory(
                "listenarr-manual-scan-failure-dest");
            var sourceDir = CreateTempDirectory(
                "listenarr-manual-scan-failure-src");
            var source = Path.Join(sourceDir, "book.mp3");
            await File.WriteAllTextAsync(source, "audio");
            var book = new Audiobook
            {
                Id = 334,
                Title = "Focused Scan Failure"
            };
            string? persistedBasePath = null;
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(candidate => candidate.GetByIdAsync(book.Id))
                .ReturnsAsync(() => new Audiobook
                {
                    Id = book.Id,
                    Title = book.Title,
                    BasePath = persistedBasePath
                });
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService.Setup(candidate => candidate.CheckAudiobookFileOwnershipAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available));
            fileService.Setup(candidate => candidate.RegisterPublishedGenerationWithBasePathAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<AudiobookFileOwnershipCheckResult>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    It.IsAny<string>(),
                    "manual-import",
                    It.IsAny<CancellationToken>()))
                .Returns<Audiobook, AudiobookFileOwnershipCheckResult, IAudiobookFileRegistrationLease, string, string?, CancellationToken>((
                    audiobook,
                    _,
                    registrationLease,
                    authoritativeBasePath,
                    _,
                    _) =>
                {
                    if (!registrationLease.MatchesCurrentPublication()
                        || !registrationLease.PrepareCleanupRecovery(audiobook.Id))
                    {
                        return Task.FromResult(false);
                    }

                    persistedBasePath = authoritativeBasePath;
                    audiobook.BasePath = authoritativeBasePath;
                    var completion = registrationLease.CompletePublication();
                    return Task.FromResult(completion is
                        RegistrationPublicationCompletion.Completed or
                        RegistrationPublicationCompletion.CommittedCleanupPending);
                });
            var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);
            scanQueue.Setup(service => service.EnqueueScanAsync(
                    It.IsAny<ScanEnqueueCommand>()))
                .ThrowsAsync(new IOException("simulated scan queue failure"));
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = destinationRoot,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}"
                },
                repoMock: repository,
                scanMock: scanQueue,
                audiobookFileService: fileService.Object);
            var request = new ManualImportRequestDto
            {
                Path = sourceDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = source,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var action = await controller.Start(request);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                action.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;
            Assert.Equal(
                1,
                Assert.IsType<int>(payload.GetType()
                    .GetProperty("importedCount")!
                    .GetValue(payload)));
            var results = Assert.IsAssignableFrom<
                IEnumerable<ManualImportResultDto>>(
                payload.GetType().GetProperty("results")!.GetValue(payload));
            Assert.True(Assert.Single(results).Success);
            Assert.True(File.Exists(source));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(
                    destinationRoot,
                    "Focused Scan Failure.mp3")));
            scanQueue.Verify(service => service.EnqueueScanAsync(
                It.IsAny<ScanEnqueueCommand>()), Times.Once);
            var reloaded = await repository.Object.GetByIdAsync(book.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(destinationRoot, reloaded.BasePath);
            repository.Verify(
                candidate => candidate.UpdateAsync(It.IsAny<Audiobook>()),
                Times.Never);
        }

        [Fact]
        public async Task InteractiveManualImport_AsinTagFailureAfterMove_RemainsSuccessful()
        {
            var destinationRoot = CreateTempDirectory(
                "listenarr-manual-asin-tag-dest");
            var sourceDir = CreateTempDirectory(
                "listenarr-manual-asin-tag-src");
            var source = Path.Join(sourceDir, "book.mp3");
            await File.WriteAllTextAsync(source, "audio");
            var book = new Audiobook
            {
                Id = 335,
                Title = "Tagged Book",
                Asin = "B000TEST",
                BasePath = destinationRoot
            };
            var metadata = new Mock<IMetadataService>();
            metadata.Setup(service => service.ExtractFileMetadataAsync(source))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = book.Title,
                    Format = "mp3",
                    BitRate = 128000
                });
            metadata.Setup(service => service.WriteAsinTagAsync(
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    book.Asin))
                .ThrowsAsync(new IOException("simulated tag failure"));
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = destinationRoot,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}"
                },
                metadataMock: metadata);
            var request = new ManualImportRequestDto
            {
                Path = sourceDir,
                Mode = "interactive",
                Action = FileAction.Move,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = source,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var action = await controller.Start(request);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                action.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;
            Assert.Equal(
                1,
                Assert.IsType<int>(payload.GetType()
                    .GetProperty("importedCount")!
                    .GetValue(payload)));
            var results = Assert.IsAssignableFrom<IEnumerable<ManualImportResultDto>>(
                payload.GetType().GetProperty("results")!.GetValue(payload));
            Assert.True(Assert.Single(results).Success);
            Assert.False(File.Exists(source));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(
                    Path.Join(destinationRoot, "Tagged Book.mp3")));
            metadata.Verify(service => service.WriteAsinTagAsync(
                It.IsAny<IAudiobookFileRegistrationLease>(),
                book.Asin), Times.Once);
            metadata.Verify(service => service.WriteAsinTagAsync(
                It.IsAny<string>(),
                It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task InteractiveManualImport_CompanionPass_SkipsDifferentAudiobookAudioInSameFolder()
        {
            var destinationRoot = CreateTempDirectory("listenarr-manual-mixed-dest");
            var sourceDir = CreateTempDirectory("listenarr-manual-mixed-src");

            var book = new Audiobook { Id = 334, Title = "Companion Book", BasePath = destinationRoot };

            var selectedAudio = Path.Join(sourceDir, "Companion Book.mp3");
            var foreignAudio = Path.Join(sourceDir, "Different Book.mp3");
            var coverFile = Path.Join(sourceDir, "cover.jpg");
            await File.WriteAllTextAsync(selectedAudio, "selected");
            await File.WriteAllTextAsync(foreignAudio, "foreign");
            await File.WriteAllTextAsync(coverFile, "cover");

            var controller = GetController(book, new ApplicationSettings
            {
                OutputPath = destinationRoot,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                ImportBlacklistExtensions = new System.Collections.Generic.List<string>()
            });

            var request = new ManualImportRequestDto
            {
                Path = sourceDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                IncludeCompanionFiles = true,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = selectedAudio, MatchedAudiobookId = book.Id }
                }
            };

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            Assert.True(File.Exists(Path.Join(destinationRoot, "Companion Book.mp3")));
            Assert.True(File.Exists(Path.Join(destinationRoot, "cover.jpg")));
            Assert.False(File.Exists(Path.Join(destinationRoot, "Different Book.mp3")));
        }

        [Fact]
        public async Task InteractiveManualImport_RequestCancelledAfterCommittedMutationReturnsCommittedResultAndQueuesFocusedScan()
        {
            var basePath = CreateTempDirectory("listenarr-manual-post-mutation-cancel-dst");
            var srcDir = CreateTempDirectory("listenarr-manual-post-mutation-cancel-src");
            var source = Path.Join(srcDir, "book.mp3");
            await File.WriteAllTextAsync(source, "audio");
            var book = new Audiobook
            {
                Id = 500,
                Title = "Post Mutation Cancellation",
                BasePath = basePath
            };
            using var cancellation = new CancellationTokenSource();
            var actualMover = CreateMarkerlessFileMover();
            var fileMover = new Mock<IFileMover>();
            fileMover.Setup(mover => mover.PrepareActionForRegistrationAsync(
                    FileAction.Copy,
                    source,
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<FileAction, string, string, Guid>(async (action, sourcePath, destination, operationId) =>
                {
                    var lease = await actualMover.PrepareActionForRegistrationAsync(
                        action,
                        sourcePath,
                        destination,
                        operationId);
                    cancellation.Cancel();
                    return lease;
                });
            var scanMock = GetScanMock();
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = basePath,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}"
                },
                scanMock: scanMock,
                fileMover: fileMover.Object);
            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = source,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var action = await controller.Start(request, cancellation.Token);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                action.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;
            Assert.Equal(
                1,
                Assert.IsType<int>(payload.GetType()
                    .GetProperty("importedCount")!
                    .GetValue(payload)));
            Assert.Equal(
                1,
                Assert.IsType<int>(payload.GetType()
                    .GetProperty("totalCount")!
                    .GetValue(payload)));
            Assert.True(
                Assert.IsType<bool>(payload.GetType()
                    .GetProperty("stoppedByCancellation")!
                    .GetValue(payload)));
            Assert.True(File.Exists(Path.Join(
                basePath,
                "Post Mutation Cancellation.mp3")));
            scanMock.Verify(service => service.EnqueueScanAsync(
                It.Is<ScanEnqueueCommand>(command =>
                    command.Audiobook.Id == book.Id
                    && command.Path == basePath
                    && command.PathIdentity.HasValue
                    && command.PhysicalIdentity.HasValue
                    && !command.IsAuthoritativeScope)), Times.Once);
        }

        [Fact]
        public async Task InteractiveManualImport_FocusedScanCancellationAfterCommittedMutation_ReturnsCommittedResult()
        {
            var basePath = CreateTempDirectory("listenarr-manual-scan-cancel-dst");
            var srcDir = CreateTempDirectory("listenarr-manual-scan-cancel-src");
            var source = Path.Join(srcDir, "book.mp3");
            await File.WriteAllTextAsync(source, "audio");
            var book = new Audiobook
            {
                Id = 504,
                Title = "Post Commit Scan Cancellation",
                BasePath = basePath
            };
            var scanMock = GetScanMock();
            scanMock.Setup(service => service.EnqueueScanAsync(
                    It.IsAny<ScanEnqueueCommand>()))
                .ThrowsAsync(new TaskCanceledException(
                    "Injected post-commit focused scan cancellation."));
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = basePath,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}"
                },
                scanMock: scanMock);
            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = source,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var action = await controller.Start(request);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                action.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;
            Assert.Equal(
                1,
                Assert.IsType<int>(payload.GetType()
                    .GetProperty("importedCount")!
                    .GetValue(payload)));
            Assert.True(File.Exists(Path.Join(
                basePath,
                "Post Commit Scan Cancellation.mp3")));
            scanMock.Verify(service => service.EnqueueScanAsync(
                It.Is<ScanEnqueueCommand>(command =>
                    command.Audiobook.Id == book.Id
                    && !command.IsAuthoritativeScope)), Times.Once);
        }

        [Fact]
        public async Task InteractiveManualImport_RequestCancelledAfterFirstCommittedItem_ReturnsPartialCommitAndDoesNotStartSecondItem()
        {
            var basePath = CreateTempDirectory("listenarr-manual-partial-cancel-dst");
            var srcDir = CreateTempDirectory("listenarr-manual-partial-cancel-src");
            var firstSource = Path.Join(srcDir, "first.mp3");
            var secondSource = Path.Join(srcDir, "second.mp3");
            await File.WriteAllTextAsync(firstSource, "first-audio");
            await File.WriteAllTextAsync(secondSource, "second-audio");
            var book = new Audiobook
            {
                Id = 502,
                Title = "Partial Cancellation",
                BasePath = basePath
            };
            using var cancellation = new CancellationTokenSource();
            var actualMover = CreateMarkerlessFileMover();
            var prepareCount = 0;
            var fileMover = new Mock<IFileMover>(MockBehavior.Strict);
            fileMover.Setup(mover => mover.PrepareActionForRegistrationAsync(
                    FileAction.Copy,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<FileAction, string, string, Guid>(async (action, sourcePath, destination, operationId) =>
                {
                    prepareCount++;
                    return await actualMover.PrepareActionForRegistrationAsync(
                        action,
                        sourcePath,
                        destination,
                        operationId);
                });
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService.Setup(service => service.CheckAudiobookFileOwnershipAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available));
            var registrationCount = 0;
            fileService.Setup(service => service.RegisterPublishedGenerationWithBasePathAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<AudiobookFileOwnershipCheckResult>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    Audiobook audiobook,
                    AudiobookFileOwnershipCheckResult _,
                    IAudiobookFileRegistrationLease registrationLease,
                    string authoritativeBasePath,
                    string? _,
                    CancellationToken _) =>
                {
                    registrationCount++;
                    if (!registrationLease.PrepareCleanupRecovery(audiobook.Id))
                    {
                        return false;
                    }

                    audiobook.BasePath = authoritativeBasePath;
                    if (registrationCount == 1)
                    {
                        cancellation.Cancel();
                    }
                    return true;
                });
            var metadata = new Mock<IMetadataService>();
            metadata.Setup(service => service.ExtractFileMetadataAsync(firstSource))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = book.Title,
                    Format = "mp3",
                    TrackNumber = 1
                });
            metadata.Setup(service => service.ExtractFileMetadataAsync(secondSource))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = book.Title,
                    Format = "mp3",
                    TrackNumber = 2
                });
            var scanMock = GetScanMock();
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = basePath,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}",
                    MultiFileNamingPattern = "{Title} - {ChapterNumber}"
                },
                scanMock: scanMock,
                fileMover: fileMover.Object,
                audiobookFileService: fileService.Object,
                metadataMock: metadata);
            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = firstSource,
                        MatchedAudiobookId = book.Id
                    },
                    new ManualImportItemDto
                    {
                        FullPath = secondSource,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var action = await controller.Start(request, cancellation.Token);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                action.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;
            Assert.Equal(
                1,
                Assert.IsType<int>(payload.GetType()
                    .GetProperty("importedCount")!
                    .GetValue(payload)));
            Assert.Equal(
                2,
                Assert.IsType<int>(payload.GetType()
                    .GetProperty("totalCount")!
                    .GetValue(payload)));
            Assert.True(
                Assert.IsType<bool>(payload.GetType()
                    .GetProperty("stoppedByCancellation")!
                    .GetValue(payload)));
            Assert.Equal(1, prepareCount);
            Assert.Equal(1, registrationCount);
            Assert.Equal("second-audio", await File.ReadAllTextAsync(secondSource));
            scanMock.Verify(service => service.EnqueueScanAsync(
                It.Is<ScanEnqueueCommand>(command =>
                    command.Audiobook.Id == book.Id
                    && command.Path == basePath
                    && !command.IsAuthoritativeScope)), Times.Once);
        }

        [Fact]
        public async Task InteractiveManualImport_FailedMove_DoesNotReserveDestinationForLaterItems()
        {
            var basePath = CreateTempDirectory("listenarr-manual-failed-reservation-dst");
            var srcDir = CreateTempDirectory("listenarr-manual-failed-reservation-src");

            var firstBook = new Audiobook { Id = 501, Title = "Collision Book", BasePath = basePath };
            var secondBook = new Audiobook { Id = 502, Title = "Collision Book", BasePath = basePath };

            var src1 = Path.Join(srcDir, "one.mp3");
            var src2 = Path.Join(srcDir, "two.mp3");
            await File.WriteAllTextAsync(src1, "one");
            await File.WriteAllTextAsync(src2, "two");

            var repoMock = new Mock<IAudiobookRepository>();
            repoMock.Setup(r => r.GetByIdAsync(firstBook.Id)).ReturnsAsync(firstBook);
            repoMock.Setup(r => r.GetByIdAsync(secondBook.Id)).ReturnsAsync(secondBook);
            repoMock.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var attemptedDestinations = new List<string>();
            var callCount = 0;
            var actualMover = CreateMarkerlessFileMover();
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

                    return await actualMover.PrepareActionForRegistrationAsync(
                        action,
                        source,
                        destination,
                        operationId);
                });

            var controller = GetController(firstBook, new ApplicationSettings
            {
                OutputPath = basePath,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}"
            }, repoMock, fileMover: fileMover.Object);

            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Copy,
                Items = new System.Collections.Generic.List<ManualImportItemDto>
                {
                    new ManualImportItemDto { FullPath = src1, MatchedAudiobookId = firstBook.Id },
                    new ManualImportItemDto { FullPath = src2, MatchedAudiobookId = secondBook.Id }
                }
            };

            var action = await controller.Start(request);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);

            Assert.Equal(2, attemptedDestinations.Count);
            Assert.Equal(attemptedDestinations[0], attemptedDestinations[1]);
            Assert.EndsWith("Collision Book.mp3", attemptedDestinations[1], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("(1)", Path.GetFileName(attemptedDestinations[1]), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Join(basePath, "Collision Book.mp3")));
        }

        [Fact]
        public async Task InteractiveManualImport_MoveRegistrationFailure_RetainsSourceForRetry()
        {
            var basePath = CreateTempDirectory("listenarr-manual-registration-failure-dst");
            var srcDir = CreateTempDirectory("listenarr-manual-registration-failure-src");
            var source = Path.Join(srcDir, "book.mp3");
            await File.WriteAllTextAsync(source, "audio");
            var book = new Audiobook
            {
                Id = 503,
                Title = "Registration Failure",
                BasePath = basePath
            };

            var actualMover = CreateMarkerlessFileMover();
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            mover.Setup(candidate => candidate.PrepareActionForRegistrationAsync(
                    FileAction.Move,
                    source,
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<FileAction, string, string, Guid>((action, sourcePath, destination, operationId) =>
                    actualMover.PrepareActionForRegistrationAsync(
                        action,
                        sourcePath,
                        destination,
                        operationId));
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService.Setup(candidate => candidate.CheckAudiobookFileOwnershipAsync(
                    book,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available));
            fileService.Setup(candidate => candidate.RegisterPublishedGenerationAsync(
                    book,
                    It.IsAny<AudiobookFileOwnershipCheckResult>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    "manual-import",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = basePath,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}"
                },
                fileMover: mover.Object,
                audiobookFileService: fileService.Object);
            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Move,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = source,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var action = await controller.Start(request);

            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);
            Assert.True(File.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(source));
            Assert.True(File.Exists(Path.Join(
                basePath,
                "Registration Failure.mp3")));
            mover.Verify(candidate => candidate.CompletePreparedMoveAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    It.IsAny<Guid>()),
                Times.Never);
        }

        [Fact]
        public async Task InteractiveManualImport_MoveCleanupDetectsStalePublication_RollsBackPhysicalClaim()
        {
            var basePath = CreateTempDirectory(
                "listenarr-manual-stale-cleanup-dst");
            var srcDir = CreateTempDirectory(
                "listenarr-manual-stale-cleanup-src");
            var source = Path.Join(srcDir, "book.mp3");
            await File.WriteAllTextAsync(source, "audio");
            var book = new Audiobook
            {
                Id = 504,
                Title = "Stale Cleanup",
                BasePath = basePath
            };

            var actualMover = CreateMarkerlessFileMover();
            ControllableRegistrationLease? controlledLease = null;
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            mover.Setup(candidate => candidate.PrepareActionForRegistrationAsync(
                    FileAction.Move,
                    source,
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<FileAction, string, string, Guid>(async (
                    action,
                    sourcePath,
                    destination,
                    operationId) =>
                {
                    var inner = await actualMover
                        .PrepareActionForRegistrationAsync(
                            action,
                            sourcePath,
                            destination,
                            operationId);
                    controlledLease = inner == null
                        ? null
                        : new ControllableRegistrationLease(inner);
                    return controlledLease;
                });
            mover.Setup(candidate => candidate.CompletePreparedMoveAsync(
                    source,
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
                    book,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns<Audiobook, string, string?, CancellationToken>((
                    _,
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
            fileService.Setup(candidate => candidate.RegisterPublishedGenerationWithBasePathAsync(
                    book,
                    It.IsAny<AudiobookFileOwnershipCheckResult>(),
                    It.IsAny<IAudiobookFileRegistrationLease>(),
                    It.IsAny<string>(),
                    "manual-import",
                    It.IsAny<CancellationToken>()))
                .Returns<Audiobook, AudiobookFileOwnershipCheckResult, IAudiobookFileRegistrationLease, string, string?, CancellationToken>((
                    audiobook,
                    _,
                    lease,
                    authoritativeBasePath,
                    _,
                    _) =>
                {
                    if (!lease.PrepareCleanupRecovery(audiobook.Id))
                    {
                        return Task.FromResult(false);
                    }

                    audiobook.BasePath = authoritativeBasePath;
                    registered = true;
                    registeredFile = AudiobookFile.CreateUnresolved(
                        lease.PublicPath);
                    registeredFile.Id = 42;
                    registeredFile.AudiobookId = audiobook.Id;
                    registeredFile.ApplyPhysicalObjectIdentity(
                        lease.PhysicalObjectIdentity,
                        DateTime.UtcNow);
                    return Task.FromResult(true);
                });
            fileService.Setup(candidate => candidate.RollbackPublishedGenerationIfStaleAsync(
                    book,
                    It.IsAny<IAudiobookFileRegistrationLease>()))
                .Returns<Audiobook, IAudiobookFileRegistrationLease>((_, _) =>
                {
                    registered = false;
                    return Task.CompletedTask;
                });
            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = basePath,
                    FolderNamingPattern = "",
                    FileNamingPattern = "{Title}"
                },
                fileMover: mover.Object,
                audiobookFileService: fileService.Object);
            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.Move,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = source,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var action = await controller.Start(request);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                action.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;
            Assert.Equal(
                0,
                Assert.IsType<int>(payload.GetType()
                    .GetProperty("importedCount")!
                    .GetValue(payload)));
            Assert.False(registered);
            Assert.True(File.Exists(source));
            fileService.Verify(candidate =>
                candidate.RollbackPublishedGenerationIfStaleAsync(
                    book,
                    controlledLease!),
                Times.Once);
        }

        [Fact]
        public async Task InteractiveManualImport_FileActionNone_IsACompleteNoOp()
        {
            var basePath = CreateTempDirectory("listenarr-manual-neutral-dst");
            var srcDir = CreateTempDirectory("listenarr-manual-neutral-src");
            var emptySourceDirectory = Path.Join(srcDir, "empty");
            Directory.CreateDirectory(emptySourceDirectory);

            var book = new Audiobook
            {
                Id = 126,
                Title = "Jack of Shadows",
                BasePath = basePath,
                Asin = "B000TEST"
            };
            var source = Path.Join(srcDir, "Chapter 01.mp3");
            await File.WriteAllTextAsync(source, "chapter1");

            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(candidate => candidate.GetByIdAsync(book.Id))
                .ReturnsAsync(book);
            var scanQueue = new Mock<IScanQueueService>(MockBehavior.Strict);
            var fileMover = new Mock<IFileMover>(MockBehavior.Strict);
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            var ownershipStore = new Mock<ILibraryDirectoryOwnershipStore>(MockBehavior.Strict);
            var metadata = new Mock<IMetadataService>(MockBehavior.Strict);
            var sourceSnapshot = Directory.GetFileSystemEntries(
                    srcDir,
                    "*",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var destinationSnapshot = Directory.GetFileSystemEntries(
                    basePath,
                    "*",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var originalBasePath = book.BasePath;

            var controller = GetController(
                book,
                new ApplicationSettings
                {
                    OutputPath = basePath,
                    FolderNamingPattern = "{Author}/{Title}",
                    FileNamingPattern = "{Title}",
                    MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
                },
                repository,
                scanQueue,
                fileMover.Object,
                fileService.Object,
                directoryOwnershipStore: ownershipStore.Object,
                metadataMock: metadata);
            var request = new ManualImportRequestDto
            {
                Path = srcDir,
                Mode = "interactive",
                Action = FileAction.None,
                IncludeCompanionFiles = true,
                CleanupEmptySourceFolders = true,
                Items =
                [
                    new ManualImportItemDto
                    {
                        FullPath = source,
                        MatchedAudiobookId = book.Id
                    }
                ]
            };

            var action = await controller.Start(request);

            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                action.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;
            Assert.Equal(
                0,
                Assert.IsType<int>(payload.GetType()
                    .GetProperty("importedCount")!
                    .GetValue(payload)));
            var returnedResults = Assert.IsAssignableFrom<IEnumerable<ManualImportResultDto>>(
                payload.GetType().GetProperty("results")!.GetValue(payload));
            var result = Assert.Single(returnedResults);
            Assert.False(result.Success);
            Assert.Null(result.DestinationPath);
            Assert.Equal(source, result.SourcePath);
            var skippedProperty = typeof(ManualImportResultDto).GetProperty("Skipped");
            Assert.NotNull(skippedProperty);
            Assert.True(Assert.IsType<bool>(skippedProperty.GetValue(result)));

            Assert.Equal(originalBasePath, book.BasePath);
            Assert.Equal(
                sourceSnapshot,
                Directory.GetFileSystemEntries(srcDir, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal));
            Assert.Equal(
                destinationSnapshot,
                Directory.GetFileSystemEntries(basePath, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal));
            Assert.True(Directory.Exists(emptySourceDirectory));
            repository.Verify(candidate => candidate.GetByIdAsync(book.Id), Times.Once);
            repository.VerifyNoOtherCalls();
            scanQueue.VerifyNoOtherCalls();
            fileMover.VerifyNoOtherCalls();
            fileService.VerifyNoOtherCalls();
            ownershipStore.VerifyNoOtherCalls();
            metadata.VerifyNoOtherCalls();
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
