using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportCompanionImporterTests")]
[Trait("Category", "Unit")]
public sealed class ManualImportCompanionImporterTests : BaseTests
{
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
                cancellationToken: cancellation.Token));

            Assert.True(File.Exists(companionSource));
            Assert.False(File.Exists(Path.Join(destinationDirectory, "cover.jpg")));
            mover.Verify(
                service => service.PerformActionOn(
                    It.IsAny<FileAction>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<FilePublicationSourceProof>()),
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
    public async Task ImportAsync_AudioMoveRegistrationFails_DoesNotRetireSource()
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
            mover.Setup(service => service.PrepareActionForRegistrationAsync(
                    FileAction.Move,
                    companionSource,
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<string?>(),
                    It.IsAny<FilePublicationSourceProof>()))
                .ReturnsAsync(lease.Object);
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

            var imported = await importer.ImportAsync(
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
                importBlacklist: []);

            Assert.Equal(0, imported);
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
            var mover = new Mock<IFileMover>();
            mover.Setup(service => service.PerformActionOn(
                    FileAction.Copy,
                    companionSource,
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<FilePublicationSourceProof>()))
                .Callback<FileAction, string, string?, Guid, FilePublicationSourceProof>((_, _, destination, _, _) =>
                    capturedDestination = destination)
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
                FileAction.Copy,
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
                importBlacklist: []);

            Assert.Equal(1, imported);
            Assert.Equal(
                Path.Join(destinationDirectory, "cover.jpg"),
                capturedDestination);
            Assert.True(FileSystemPathIdentity.IsSameOrInside(
                capturedDestination!,
                destinationDirectory,
                FileSystemPathSemantics.CurrentHostDefault));
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
