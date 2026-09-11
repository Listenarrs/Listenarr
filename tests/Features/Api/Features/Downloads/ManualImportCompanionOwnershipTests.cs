using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportCompanionOwnershipTests")]
[Trait("Category", "Integration")]
public sealed class ManualImportCompanionOwnershipTests : BaseTests
{
    [Theory]
    [InlineData("bonus.m4b")]
    [InlineData("cover.jpg")]
    public async Task ImportAsync_DestinationOwnedByAnotherAudiobook_DoesNotWriteCompanion(
        string companionFileName)
    {
        Init();
        var testRoot = FileService.GetTempDirectory(
            $"manual-import-companion-owned-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Join(testRoot, "source");
        var destinationDirectory = Path.Join(testRoot, "library", "target-book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var selectedSource = Path.Join(sourceDirectory, "book.m4b");
        var companionSource = Path.Join(sourceDirectory, companionFileName);
        var selectedDestination = Path.Join(destinationDirectory, "book.m4b");
        var companionDestination = Path.Join(destinationDirectory, companionFileName);
        await File.WriteAllTextAsync(selectedSource, "selected audio");
        await File.WriteAllTextAsync(companionSource, "companion content");

        var targetAudiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Target Book")
                .WithAuthor("Target Author")
                .WithBasePath(destinationDirectory)
                .Build());
        var otherAudiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Other Book")
                .WithAuthor("Other Author")
                .WithBasePath(destinationDirectory)
                .Build());
        var identityResolver = _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>();
        var identity = await identityResolver.ResolveAsync(
            otherAudiobook,
            companionDestination);
        Assert.Equal(PathIdentityState.Valid, identity.State);
        var ownedFile = AudiobookFile.CreateUnresolved(companionDestination);
        ownedFile.AudiobookId = otherAudiobook.Id;
        ownedFile.ApplyPathIdentity(companionDestination, identity);
        var claim = await _audiobookFileRepository.ClaimAsync(ownedFile);
        Assert.Equal(AudiobookFileClaimOutcome.Created, claim.Outcome);

        var metadata = new AudioMetadata
        {
            Title = "Target Book",
            Artist = "Target Author",
            Duration = TimeSpan.FromMinutes(10),
            Format = "m4b"
        };
        var metadataService = new Mock<IMetadataService>(MockBehavior.Strict);
        if (FileUtils.IsAudioFile(companionSource))
        {
            metadataService
                .Setup(service => service.ExtractFileMetadataAsync(companionSource))
                .ReturnsAsync(metadata);
        }

        var mover = new Mock<IFileMover>(MockBehavior.Strict);
        var sourceCapability = new Mock<IFilePublicationSourceCapability>(MockBehavior.Strict);
        sourceCapability
            .Setup(service => service.CheckAsync(
                companionSource,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                FilePublicationSourceCapabilityResult.SupportedForProof(
                    new FilePublicationSourceProof(
                        "test-source-generation",
                        1,
                        new string('A', 64))));
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
            .ReturnsAsync([]);
        var semanticsResolver = _provider.GetRequiredService<IFileSystemSemanticsResolver>();
        var fileService = _provider.GetRequiredService<IAudiobookFileService>();
        var importer = new ManualImportCompanionImporter(
            metadataService.Object,
            mover.Object,
            sourceCapability.Object,
            new LocalFileSystem(),
            ownershipStore.Object,
            NullLogger<ManualImportCompanionImporter>.Instance,
            fileService);
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
                MatchedAudiobookId = targetAudiobook.Id
            }
        };
        var results = new[]
        {
            new ManualImportResultDto
            {
                Success = true,
                SourcePath = selectedSource,
                DestinationPath = selectedDestination,
                Audiobook = targetAudiobook
            }
        };

        var imported = await importer.ImportAsync(
            FileAction.Copy,
            items,
            results,
            sourceDirectory,
            selectedProfiles,
            tracker,
            sourceResolution.Semantics,
            new Dictionary<int, FileSystemSemanticsResolution>
            {
                [targetAudiobook.Id] = destinationResolution
            },
            importBlacklist: [],
            rootFolders: [
                new RootFolder
                {
                    Id = 1,
                    Name = "library",
                    Path = Path.Join(testRoot, "library")
                }
            ]);

        Assert.Equal(0, imported);
        Assert.True(File.Exists(companionSource));
        Assert.False(File.Exists(companionDestination));
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
    }

    [Fact]
    public async Task ImportAsync_CompanionUnderAConfiguredRoot_IsAuthorizedAgainstThatRootAndPublished()
    {
        Init();
        var testRoot = FileService.GetTempDirectory(
            $"manual-import-companion-authorized-{Guid.NewGuid():N}");
        var libraryRoot = Path.Join(testRoot, "library");
        var sourceDirectory = Path.Join(testRoot, "source");
        var destinationDirectory = Path.Join(libraryRoot, "Target Author", "Target Book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var rootFolder = await AddAuthorizedRootAsync(libraryRoot, "Companion library");
        var selectedSource = Path.Join(sourceDirectory, "book.m4b");
        var companionSource = Path.Join(sourceDirectory, "cover.jpg");
        var selectedDestination = Path.Join(destinationDirectory, "book.m4b");
        var companionDestination = Path.Join(destinationDirectory, "cover.jpg");
        await File.WriteAllTextAsync(selectedSource, "selected audio");
        await File.WriteAllTextAsync(companionSource, "companion content");

        var targetAudiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Target Book")
                .WithAuthor("Target Author")
                .WithBasePath(destinationDirectory)
                .Build());

        var lease = new Mock<IAudiobookFileRegistrationLease>(MockBehavior.Strict);
        lease
            .Setup(registration => registration.PrepareCleanupRecovery(targetAudiobook.Id))
            .Returns(true);
        lease
            .Setup(registration => registration.CompletePublication())
            .Returns(RegistrationPublicationCompletion.Completed);
        lease.Setup(registration => registration.Dispose());
        string? preparedDestination = null;
        var mover = new Mock<IFileMover>(MockBehavior.Strict);
        mover
            .Setup(service => service.PrepareActionForRegistrationDetailedAsync(
                It.IsAny<FilePublicationPlan>(),
                companionSource,
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<FilePublicationSourceProof>(),
                It.IsAny<bool>(),
                It.IsAny<int?>()))
            .Callback<
                FilePublicationPlan,
                string,
                string,
                Guid,
                string?,
                FilePublicationSourceProof,
                bool,
                int?>((_, source, destination, _, _, _, _, _) =>
            {
                preparedDestination = destination;
                File.Copy(source, destination, overwrite: true);
            })
            .ReturnsAsync(new FilePublicationPreparationResult(
                FilePublicationOutcome.Success,
                FileAction.Copy,
                FileAction.Copy,
                FilePublicationSourceDisposition.Unchanged,
                lease.Object));
        var sourceCapability = new Mock<IFilePublicationSourceCapability>(MockBehavior.Strict);
        sourceCapability
            .Setup(service => service.CheckAsync(
                companionSource,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                FilePublicationSourceCapabilityResult.SupportedForProof(
                    new FilePublicationSourceProof(
                        "test-source-generation",
                        1,
                        new string('A', 64))));

        var semanticsResolver = _provider.GetRequiredService<IFileSystemSemanticsResolver>();
        var importer = new ManualImportCompanionImporter(
            Mock.Of<IMetadataService>(),
            mover.Object,
            sourceCapability.Object,
            new LocalFileSystem(),
            _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>(),
            NullLogger<ManualImportCompanionImporter>.Instance,
            _provider.GetRequiredService<IAudiobookFileService>());
        var tracker = new ManualImportDestinationTracker(
            new LocalFileSystem(),
            Mock.Of<IFilePublicationSourceCapability>());
        var sourceResolution = await semanticsResolver.ResolveAsync(sourceDirectory);
        var destinationResolution = await semanticsResolver.ResolveAsync(destinationDirectory);
        var items = new[]
        {
            new ManualImportItemDto
            {
                FullPath = selectedSource,
                MatchedAudiobookId = targetAudiobook.Id
            }
        };
        var results = new[]
        {
            new ManualImportResultDto
            {
                Success = true,
                SourcePath = selectedSource,
                DestinationPath = selectedDestination,
                Audiobook = targetAudiobook
            }
        };

        var outcome = await importer.ImportWithOutcomeAsync(
            FileAction.Copy,
            items,
            results,
            sourceDirectory,
            selectedAudioProfiles: [],
            tracker,
            sourceResolution.Semantics,
            new Dictionary<int, FileSystemSemanticsResolution>
            {
                [targetAudiobook.Id] = destinationResolution
            },
            importBlacklist: [],
            compatibilityBatchId: Guid.NewGuid(),
            rootFolders: [rootFolder]);

        // The ownership store here is the real one, so EnsureCreatedHierarchyAsync runs
        // LibraryDirectoryOwnershipBoundaryAuthorizer.AuthorizeAsync against the RootFolders
        // table. That match is by equivalence, so handing it the book folder throws and the
        // companion is skipped. Only a boundary that is a configured root gets this far.
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.ImportedCount);
        Assert.Equal(companionDestination, preparedDestination);
        Assert.True(File.Exists(companionDestination));
        Assert.True(File.Exists(companionSource));
    }
}
