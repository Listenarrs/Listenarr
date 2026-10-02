using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportCompanionImporterTests")]
[Trait("Category", "Unit")]
public sealed class ManualImportCompanionImporterTests : BaseTests
{
    private static RootFolder[] RootFoldersFor(string testRoot) =>
        [new RootFolder { Id = 1, Name = "library", Path = Path.Join(testRoot, "library") }];

    private static IFilePublicationSourceCapability SupportedSourceCapability()
    {
        var capability = new Mock<IFilePublicationSourceCapability>(MockBehavior.Strict);
        capability
            .Setup(service => service.CheckAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                FilePublicationSourceCapabilityResult.SupportedForProof(
                    new FilePublicationSourceProof(
                        "test-source-generation",
                        1,
                        new string('A', 64))));
        return capability.Object;
    }

    [Fact]
    public async Task ImportAsync_CanceledAfterOwnershipPreparation_DoesNotMutateCompanionFile()
    {
        var testRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"manual-import-companion-canceled-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Join(testRoot, "source");
        var destinationDirectory = Path.Join(testRoot, "library", "book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var audioSource = Path.Join(sourceDirectory, "book.m4b");
        var companionSource = Path.Join(sourceDirectory, "cover.jpg");
        var audioDestination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(audioSource, "audio");
        await File.WriteAllTextAsync(companionSource, "image");

        try
        {
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
            var audiobook = new Audiobook
            {
                Id = 42,
                BasePath = destinationDirectory
            };
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService
                .Setup(service => service.CheckAudiobookFileOwnershipAsync(
                    audiobook,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available));
            var semanticsResolver = new FileSystemSemanticsResolver();
            var importer = new ManualImportCompanionImporter(
                Mock.Of<IMetadataService>(),
                mover.Object,
                SupportedSourceCapability(),
                new LocalFileSystem(),
                ownershipStore.Object,
                NullLogger<ManualImportCompanionImporter>.Instance,
                fileService.Object);
            var tracker = new ManualImportDestinationTracker(
                new LocalFileSystem(),
                Mock.Of<IFilePublicationSourceCapability>());
            var sourceResolution = await semanticsResolver.ResolveAsync(sourceDirectory);
            var destinationResolution = await semanticsResolver.ResolveAsync(destinationDirectory);
            var items = new[]
            {
                new ManualImportItemDto
                {
                    FullPath = audioSource,
                    MatchedAudiobookId = audiobook.Id
                }
            };
            var results = new[]
            {
                new ManualImportResultDto
                {
                    Success = true,
                    SourcePath = audioSource,
                    DestinationPath = audioDestination,
                    Audiobook = audiobook
                }
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(
                FileAction.Copy,
                items,
                results,
                sourceDirectory,
                selectedAudioProfiles: [],
                tracker,
                sourceResolution.Semantics,
                new Dictionary<int, FileSystemSemanticsResolution>
                {
                    [audiobook.Id] = destinationResolution
                },
                importBlacklist: [],
                rootFolders: RootFoldersFor(testRoot),
                cancellationToken: cancellation.Token));

            Assert.True(File.Exists(companionSource));
            Assert.False(File.Exists(Path.Join(destinationDirectory, "cover.jpg")));
            mover.Verify(
                service => service.PrepareActionForRegistrationDetailedAsync(
                    It.IsAny<FilePublicationPlan>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<string?>(),
                    It.IsAny<FilePublicationSourceProof>(),
                    It.IsAny<bool>(),
                    It.IsAny<int?>()),
                Times.Never);
            fileService.VerifyAll();
            ownershipStore.VerifyAll();
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportWithOutcomeAsync_AudioMoveRegistrationFails_VetoesBatchCleanupAndDoesNotRetireSource()
    {
        var testRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"manual-import-companion-registration-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Join(testRoot, "source");
        var destinationDirectory = Path.Join(testRoot, "library", "book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var selectedSource = Path.Join(sourceDirectory, "book.m4b");
        var companionSource = Path.Join(sourceDirectory, "bonus.m4b");
        var selectedDestination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(selectedSource, "selected");
        await File.WriteAllTextAsync(companionSource, "companion");

        try
        {
            var metadata = new AudioMetadata
            {
                Title = "Book",
                Album = "Book",
                Artist = "Author",
                Duration = TimeSpan.FromMinutes(10),
                Format = "m4b"
            };
            var metadataService = new Mock<IMetadataService>(MockBehavior.Strict);
            metadataService
                .Setup(service => service.ExtractFileMetadataAsync(companionSource))
                .ReturnsAsync(metadata);
            var lease = new Mock<IAudiobookFileRegistrationLease>(MockBehavior.Strict);
            lease.Setup(service => service.Dispose());
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            mover.Setup(service => service.PrepareActionForRegistrationDetailedAsync(
                    It.Is<FilePublicationPlan>(plan =>
                        plan.EffectiveAction == FileAction.Move),
                    companionSource,
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<string?>(),
                    It.IsAny<FilePublicationSourceProof>(),
                    false,
                    null))
                .ReturnsAsync(new FilePublicationPreparationResult(
                    FilePublicationOutcome.Success,
                    FileAction.Move,
                    FileAction.Move,
                    FilePublicationSourceDisposition.Retired,
                    lease.Object));
            var audiobook = new Audiobook
            {
                Id = 43,
                Title = "Book",
                Authors = ["Author"],
                BasePath = destinationDirectory
            };
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService
                .Setup(service => service.CheckAudiobookFileOwnershipAsync(
                    audiobook,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available));
            fileService
                .Setup(service => service.RegisterPublishedGenerationAsync(
                    audiobook,
                    It.IsAny<AudiobookFileOwnershipCheckResult>(),
                    lease.Object,
                    "manual-import-companion",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var ownershipStore = new Mock<ILibraryDirectoryOwnershipStore>();
            ownershipStore
                .Setup(store => store.EnsureCreatedHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var semanticsResolver = new FileSystemSemanticsResolver();
            var importer = new ManualImportCompanionImporter(
                metadataService.Object,
                mover.Object,
                SupportedSourceCapability(),
                new LocalFileSystem(),
                ownershipStore.Object,
                NullLogger<ManualImportCompanionImporter>.Instance,
                fileService.Object);
            var tracker = new ManualImportDestinationTracker(
                new LocalFileSystem(),
                Mock.Of<IFilePublicationSourceCapability>());
            var sourceResolution = await semanticsResolver.ResolveAsync(sourceDirectory);
            var destinationResolution = await semanticsResolver.ResolveAsync(
                Path.GetDirectoryName(selectedDestination)!);
            var selectedProfiles = new[]
            {
                FileUtils.CreateAudioMatchProfile(selectedSource, metadata)
            };
            var items = new[]
            {
                new ManualImportItemDto
                {
                    FullPath = selectedSource,
                    MatchedAudiobookId = audiobook.Id
                }
            };
            var results = new[]
            {
                new ManualImportResultDto
                {
                    Success = true,
                    SourcePath = selectedSource,
                    DestinationPath = selectedDestination,
                    Audiobook = audiobook
                }
            };

            var outcome = await importer.ImportWithOutcomeAsync(
                FileAction.Move,
                items,
                results,
                sourceDirectory,
                selectedProfiles,
                tracker,
                sourceResolution.Semantics,
                new Dictionary<int, FileSystemSemanticsResolution>
                {
                    [audiobook.Id] = destinationResolution
                },
                importBlacklist: [],
                compatibilityBatchId: Guid.NewGuid(),
                rootFolders: RootFoldersFor(testRoot));

            Assert.Equal(0, outcome.ImportedCount);
            Assert.False(outcome.Succeeded);
            Assert.True(File.Exists(companionSource));
            mover.Verify(service => service.CompletePreparedMoveAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IAudiobookFileRegistrationLease>(),
                It.IsAny<Guid>()), Times.Never);
            mover.VerifyAll();
            fileService.VerifyAll();
            lease.VerifyAll();
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportWithOutcomeAsync_VerifiedCleanupCompanion_PendingBatchCleanupCountsAsSuccess()
    {
        var testRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"manual-import-companion-verified-cleanup-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Join(testRoot, "source");
        var destinationDirectory = Path.Join(testRoot, "library", "book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var selectedSource = Path.Join(sourceDirectory, "book.m4b");
        var companionSource = Path.Join(sourceDirectory, "cover.jpg");
        var selectedDestination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(selectedSource, "selected");
        await File.WriteAllTextAsync(companionSource, "cover");

        try
        {
            var batchId = Guid.NewGuid();
            var publicationPlan = FilePublicationPlan.VerifiedCleanup(
                batchId,
                CompatibilityCleanupOwner.Listenarr,
                sourceRootFolderId: null,
                sourcePolicyRevision: null,
                destinationRootFolderId: 87,
                destinationPolicyRevision: 4,
                sourceStorageContractRevision: null,
                destinationStorageContractRevision: 9);
            var publicationResolver = new Mock<IFilePublicationCapabilityResolver>(
                MockBehavior.Strict);
            publicationResolver.Setup(resolver => resolver.ResolveAsync(
                    FileAction.Move,
                    companionSource,
                    It.IsAny<string>(),
                    It.IsAny<FilePublicationSourceProof>(),
                    It.IsAny<CancellationToken>(),
                    batchId,
                    CompatibilityCleanupOwner.Listenarr))
                .ReturnsAsync(publicationPlan);

            var lease = new Mock<IAudiobookFileRegistrationLease>(MockBehavior.Strict);
            lease.Setup(service => service.PrepareCleanupRecovery(42)).Returns(true);
            lease.Setup(service => service.CompletePublication())
                .Returns(RegistrationPublicationCompletion.CommittedCleanupPending);
            lease.Setup(service => service.Dispose());
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            mover.Setup(service => service.PrepareActionForRegistrationDetailedAsync(
                    publicationPlan,
                    companionSource,
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    null,
                    It.IsAny<FilePublicationSourceProof>(),
                    true,
                    42))
                .ReturnsAsync(new FilePublicationPreparationResult(
                    FilePublicationOutcome.Success,
                    FileAction.Move,
                    FileAction.Copy,
                    FilePublicationSourceDisposition.Retained,
                    lease.Object));

            var audiobook = new Audiobook
            {
                Id = 42,
                BasePath = destinationDirectory
            };
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService
                .Setup(service => service.CheckAudiobookFileOwnershipAsync(
                    audiobook,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available));
            var ownershipStore = new Mock<ILibraryDirectoryOwnershipStore>(
                MockBehavior.Strict);
            ownershipStore
                .Setup(store => store.EnsureAdditiveHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var importer = new ManualImportCompanionImporter(
                Mock.Of<IMetadataService>(),
                mover.Object,
                SupportedSourceCapability(),
                new LocalFileSystem(),
                ownershipStore.Object,
                NullLogger<ManualImportCompanionImporter>.Instance,
                fileService.Object,
                publicationResolver.Object);
            var tracker = new ManualImportDestinationTracker(
                new LocalFileSystem(),
                Mock.Of<IFilePublicationSourceCapability>());
            var semanticsResolver = new FileSystemSemanticsResolver();
            var sourceResolution = await semanticsResolver.ResolveAsync(sourceDirectory);
            var destinationResolution = await semanticsResolver.ResolveAsync(
                destinationDirectory);
            var items = new[]
            {
                new ManualImportItemDto
                {
                    FullPath = selectedSource,
                    MatchedAudiobookId = audiobook.Id
                }
            };
            var results = new[]
            {
                new ManualImportResultDto
                {
                    Success = true,
                    SourcePath = selectedSource,
                    DestinationPath = selectedDestination,
                    Audiobook = audiobook
                }
            };

            var outcome = await importer.ImportWithOutcomeAsync(
                FileAction.Move,
                items,
                results,
                sourceDirectory,
                selectedAudioProfiles: [],
                tracker,
                sourceResolution.Semantics,
                new Dictionary<int, FileSystemSemanticsResolution>
                {
                    [audiobook.Id] = destinationResolution
                },
                importBlacklist: [],
                compatibilityBatchId: batchId,
                rootFolders: RootFoldersFor(testRoot));

            Assert.Equal(1, outcome.ImportedCount);
            Assert.True(outcome.Succeeded);
            mover.Verify(service => service.CompletePreparedMoveAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IAudiobookFileRegistrationLease>(),
                It.IsAny<Guid>()), Times.Never);
            mover.VerifyAll();
            fileService.VerifyAll();
            ownershipStore.VerifyAll();
            publicationResolver.VerifyAll();
            lease.VerifyAll();
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportAsync_SelectedSourceOutsideRequestedRoot_MapsCompanionBesideSuccessfulAudioDestination()
    {
        var testRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"manual-import-companion-{Guid.NewGuid():N}");
        var requestedRoot = Path.Join(testRoot, "requested-root");
        var selectedDirectory = Path.Join(testRoot, "selected-on-another-boundary");
        var destinationDirectory = Path.Join(testRoot, "library", "book");
        Directory.CreateDirectory(requestedRoot);
        Directory.CreateDirectory(selectedDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var audioSource = Path.Join(selectedDirectory, "book.m4b");
        var companionSource = Path.Join(selectedDirectory, "cover.jpg");
        var audioDestination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(audioSource, "audio");
        await File.WriteAllTextAsync(companionSource, "image");

        try
        {
            string? capturedDestination = null;
            var publicationCommitted = false;
            var lease = new Mock<IAudiobookFileRegistrationLease>(MockBehavior.Strict);
            lease.Setup(service => service.PrepareCleanupRecovery(42))
                .Returns(true);
            lease.Setup(service => service.CompletePublication())
                .Callback(() => publicationCommitted = true)
                .Returns(RegistrationPublicationCompletion.Completed);
            lease.Setup(service => service.Dispose());
            var mover = new Mock<IFileMover>(MockBehavior.Strict);
            mover.Setup(service => service.PrepareActionForRegistrationDetailedAsync(
                    It.Is<FilePublicationPlan>(plan =>
                        plan.EffectiveAction == FileAction.Move),
                    companionSource,
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    null,
                    It.IsAny<FilePublicationSourceProof>(),
                    true,
                    42))
                .Callback<FilePublicationPlan, string, string, Guid, string?, FilePublicationSourceProof, bool, int?>((_, _, destination, _, _, _, _, _) =>
                    capturedDestination = destination)
                .ReturnsAsync(new FilePublicationPreparationResult(
                    FilePublicationOutcome.Success,
                    FileAction.Move,
                    FileAction.Move,
                    FilePublicationSourceDisposition.Retired,
                    lease.Object));
            mover.Setup(service => service.CompletePreparedMoveAsync(
                    companionSource,
                    It.IsAny<string>(),
                    lease.Object,
                    It.IsAny<Guid>()))
                .Callback(() => Assert.True(publicationCommitted))
                .ReturnsAsync(true);
            var audiobook = new Audiobook
            {
                Id = 42,
                BasePath = destinationDirectory
            };
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService
                .Setup(service => service.CheckAudiobookFileOwnershipAsync(
                    audiobook,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available));
            var semanticsResolver = new FileSystemSemanticsResolver();
            var directoryOwnershipStore = new Mock<ILibraryDirectoryOwnershipStore>();
            directoryOwnershipStore
                .Setup(store => store.EnsureCreatedHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var importer = new ManualImportCompanionImporter(
                Mock.Of<IMetadataService>(),
                mover.Object,
                SupportedSourceCapability(),
                new LocalFileSystem(),
                directoryOwnershipStore.Object,
                NullLogger<ManualImportCompanionImporter>.Instance,
                fileService.Object);
            var tracker = new ManualImportDestinationTracker(
                new LocalFileSystem(),
                Mock.Of<IFilePublicationSourceCapability>());
            var sourceResolution = await semanticsResolver.ResolveAsync(requestedRoot);
            var destinationResolution = await semanticsResolver.ResolveAsync(destinationDirectory);
            Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
            var items = new[]
            {
                new ManualImportItemDto
                {
                    FullPath = audioSource,
                    MatchedAudiobookId = audiobook.Id
                }
            };
            var results = new[]
            {
                new ManualImportResultDto
                {
                    Success = true,
                    SourcePath = audioSource,
                    DestinationPath = audioDestination,
                    Audiobook = audiobook
                }
            };

            var imported = await importer.ImportAsync(
                FileAction.Move,
                items,
                results,
                requestedRoot,
                selectedAudioProfiles: [],
                tracker,
                sourceResolution.Semantics,
                new Dictionary<int, FileSystemSemanticsResolution>
                {
                    [audiobook.Id] = destinationResolution
                },
                importBlacklist: [],
                rootFolders: RootFoldersFor(testRoot));

            mover.VerifyAll();
            lease.VerifyAll();
            Assert.Equal(1, imported);
            Assert.Equal(
                Path.Join(destinationDirectory, "cover.jpg"),
                capturedDestination);
            Assert.True(FileSystemPathIdentity.IsSameOrInside(
                capturedDestination!,
                destinationDirectory,
                FileSystemPathSemantics.CurrentHostDefault));
            mover.Verify(service => service.PerformActionOn(
                    It.IsAny<FileAction>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<FilePublicationSourceProof>()),
                Times.Never);
            fileService.VerifyAll();
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    // LibraryDirectoryOwnershipBoundaryAuthorizer matches the managed boundary against the
    // configured root folders by equivalence, not by containment, so a boundary that is merely
    // inside a root is refused. The book folder is inside a root and is not one, which is why the
    // companion pass has to hand the store the same boundary the audio file's own import already
    // hands it. Asserting the argument rather than the outcome is deliberate: the store is a mock
    // here, so the real authorizer never runs and nothing else in this file can tell the two
    // boundaries apart.
    [Fact]
    public async Task ImportAsync_ManagedBoundaryIsTheConfiguredRootFolderRatherThanTheBookFolder()
    {
        var testRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"manual-import-companion-boundary-{Guid.NewGuid():N}");
        var libraryRoot = Path.Join(testRoot, "library");
        var sourceDirectory = Path.Join(testRoot, "source");
        var destinationDirectory = Path.Join(libraryRoot, "Author", "Book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var audioSource = Path.Join(sourceDirectory, "book.m4b");
        var companionSource = Path.Join(sourceDirectory, "book.nfo");
        var audioDestination = Path.Join(destinationDirectory, "book.m4b");
        await File.WriteAllTextAsync(audioSource, "audio");
        await File.WriteAllTextAsync(companionSource, "<nfo/>");

        try
        {
            string? capturedDirectory = null;
            string? capturedBoundary = null;
            var mover = new Mock<IFileMover>();
            mover.Setup(service => service.PerformActionOn(
                    FileAction.Copy,
                    companionSource,
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<FilePublicationSourceProof>()))
                .ReturnsAsync(true);
            var audiobook = new Audiobook
            {
                Id = 42,
                BasePath = destinationDirectory
            };
            var fileService = new Mock<IAudiobookFileService>(MockBehavior.Strict);
            fileService
                .Setup(service => service.CheckAudiobookFileOwnershipAsync(
                    audiobook,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available));
            var semanticsResolver = new FileSystemSemanticsResolver();
            var directoryOwnershipStore = new Mock<ILibraryDirectoryOwnershipStore>();
            directoryOwnershipStore
                .Setup(store => store.EnsureCreatedHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    "manual-import-companion",
                    It.IsAny<Guid?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, FileSystemPathSemantics, string, Guid?, int?, CancellationToken>(
                    (directory, boundary, _, _, _, _, _) =>
                    {
                        capturedDirectory = directory;
                        capturedBoundary = boundary;
                    })
                .ReturnsAsync([]);
            // Since #864 the companion pass picks between two store calls on the publication plan,
            // and with no capability resolver injected a non-durable source takes the additive one.
            // Capture from both, so this test pins the boundary argument whichever path runs.
            directoryOwnershipStore
                .Setup(store => store.EnsureAdditiveHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, FileSystemPathSemantics, CancellationToken>(
                    (directory, boundary, _, _) =>
                    {
                        capturedDirectory = directory;
                        capturedBoundary = boundary;
                    })
                .Returns(Task.CompletedTask);
            var importer = new ManualImportCompanionImporter(
                Mock.Of<IMetadataService>(),
                mover.Object,
                SupportedSourceCapability(),
                new LocalFileSystem(),
                directoryOwnershipStore.Object,
                NullLogger<ManualImportCompanionImporter>.Instance,
                fileService.Object);
            var tracker = new ManualImportDestinationTracker(
                new LocalFileSystem(),
                Mock.Of<IFilePublicationSourceCapability>());
            var sourceResolution = await semanticsResolver.ResolveAsync(sourceDirectory);
            var destinationResolution = await semanticsResolver.ResolveAsync(destinationDirectory);
            var items = new[]
            {
                new ManualImportItemDto
                {
                    FullPath = audioSource,
                    MatchedAudiobookId = audiobook.Id
                }
            };
            var results = new[]
            {
                new ManualImportResultDto
                {
                    Success = true,
                    SourcePath = audioSource,
                    DestinationPath = audioDestination,
                    Audiobook = audiobook
                }
            };

            var imported = await importer.ImportAsync(
                FileAction.Copy,
                items,
                results,
                sourceDirectory,
                selectedAudioProfiles: [],
                tracker,
                sourceResolution.Semantics,
                new Dictionary<int, FileSystemSemanticsResolution>
                {
                    [audiobook.Id] = destinationResolution
                },
                importBlacklist: [],
                rootFolders: [
                    new RootFolder { Id = 1, Name = "library", Path = libraryRoot }
                ]);

            // The boundary handed to the ownership store is what this test exists to pin, and it is
            // captured before publication is attempted. Since #864 the publication step that follows
            // goes through IFileMover.PrepareActionForRegistrationDetailedAsync and a registration
            // lease, which this test deliberately does not stand up, so the companion does not
            // complete here and `imported` stays 0. Asserting the count would be asserting the mock
            // graph rather than the boundary. ManualImportCompanionOwnershipTests covers the
            // end-to-end path.
            Assert.NotNull(capturedBoundary);
            Assert.Equal(destinationDirectory, capturedDirectory);
            Assert.Equal(libraryRoot, capturedBoundary);
            Assert.NotEqual(destinationDirectory, capturedBoundary);
            fileService.VerifyAll();
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
