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
            importBlacklist: []);

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

    // A manual import with companion files used to drop every companion while reporting
    // success: the companion pass handed the ownership store the book folder, and the
    // authorizer only accepts a configured root folder. The controller now supplies the
    // root as BoundaryPath on the destination resolution the companion pass reads. This
    // drives that through the real controller, ownership store and authorizer, with the
    // book folder two levels inside the root, which is the shape that was refused.
    [Fact]
    public async Task Start_CompanionBesideAudioInABookFolderUnderARoot_IsAuthorizedAndPublished()
    {
        var metadata = new Mock<IMetadataService>();
        metadata
            .Setup(service => service.ExtractFileMetadataAsync(It.IsAny<string>()))
            .ReturnsAsync(new AudioMetadata
            {
                Title = "A Princess of Mars",
                Format = "m4b",
                BitRate = 128000
            });
        metadata
            .Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
            .ReturnsAsync(new AudioMetadata { Format = "m4b", Duration = TimeSpan.FromSeconds(1) });
        Init(builder => builder.WithSingleton(metadata.Object));
        var testRoot = FileService.GetTempDirectory(
            $"manual-import-companion-root-boundary-{Guid.NewGuid():N}");
        var libraryRoot = Path.Join(testRoot, "library");
        var sourceDirectory = Path.Join(testRoot, "source");
        var bookFolder = Path.Join(libraryRoot, "Edgar Rice Burroughs", "A Princess of Mars");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(bookFolder);
        await AddAuthorizedRootAsync(libraryRoot);
        var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
        settings.OutputPath = libraryRoot;
        settings.FolderNamingPattern = "";
        settings.FileNamingPattern = "{Title}";
        settings.EnableMetadataProcessing = false;
        await _applicationSettingsRepository.SaveAsync(settings);
        var audioSource = await FileService.GetFileAsync(sourceDirectory, "book.m4b", "audio");
        var companionSource = await FileService.GetFileAsync(sourceDirectory, "cover.jpg", "cover");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("A Princess of Mars")
                .WithAuthor("Edgar Rice Burroughs")
                .WithBasePath(bookFolder)
                .Build());
        var controller = ActivatorUtilities.CreateInstance<ManualImportController>(_provider);

        var action = await controller.Start(new ManualImportRequestDto
        {
            Path = sourceDirectory,
            Mode = "interactive",
            Action = FileAction.Copy,
            IncludeCompanionFiles = true,
            Items = [new ManualImportItemDto { FullPath = audioSource, MatchedAudiobookId = audiobook.Id }]
        });

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);
        Assert.Equal(1, System.Text.Json.JsonSerializer.SerializeToElement(ok.Value)
            .GetProperty("importedCount").GetInt32());
        Assert.True(File.Exists(Path.Join(bookFolder, "A Princess of Mars.m4b")));
        var companionDestination = Path.Join(bookFolder, "cover.jpg");
        Assert.True(File.Exists(companionDestination));
        Assert.Equal("cover", await File.ReadAllTextAsync(companionDestination));
        Assert.True(File.Exists(companionSource));
    }
}
