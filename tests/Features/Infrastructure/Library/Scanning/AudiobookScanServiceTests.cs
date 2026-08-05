using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Name", "AudiobookScanServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookScanServiceTests : BaseTests
{
    [Fact]
    public async Task ScanAsync_StableIdentifierBoundary_DoesNotClaimOutsideExactTitleFile()
    {
        var root = FileService.GetTempDirectory("scan-service-identifier-title");
        var identifierDirectory = Path.Join(root, "Author", "Book B012345678");
        var siblingDirectory = Path.Join(root, "Author", "Sibling");
        Directory.CreateDirectory(identifierDirectory);
        Directory.CreateDirectory(siblingDirectory);
        var inside = await FileService.GetFileAsync(identifierDirectory, "01.m4b", "audio");
        var outside = await FileService.GetFileAsync(siblingDirectory, "Book.m4b", "audio");
        var audiobookToAdd = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();
        audiobookToAdd.Asin = "B012345678";
        var audiobook = await _audiobookRepository.AddAsync(audiobookToAdd);

        var result = await ScanAsync(audiobook, root);

        var tracked = Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Equal(inside, tracked.Path);
        Assert.DoesNotContain(outside, result.AttributedFiles);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "OutsideStableIdentifierBoundary"
            && diagnostic.Path == outside);

        using var scope = _provider.CreateScope();
        var manifest = await scope.ServiceProvider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(result.Audiobook);
        var entry = Assert.Single(manifest.Entries, candidate =>
            candidate.EntryType == MoveJobEntryType.File);
        Assert.Equal(Path.GetFileName(inside), entry.RelativePath);
    }

    [Fact]
    public async Task ScanAsync_StableIdentifierBoundary_DoesNotClaimOutsideMetadataMatch()
    {
        var root = FileService.GetTempDirectory("scan-service-identifier-metadata");
        var identifierDirectory = Path.Join(root, "Author", "Book B012345678");
        var siblingDirectory = Path.Join(root, "Author", "Sibling");
        Directory.CreateDirectory(identifierDirectory);
        Directory.CreateDirectory(siblingDirectory);
        var inside = await FileService.GetFileAsync(identifierDirectory, "01.m4b", "audio");
        var outside = await FileService.GetFileAsync(siblingDirectory, "unrelated.m4b", "audio");
        var metadata = new Mock<IMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.ExtractFileMetadataAsync(
                It.IsAny<MetadataFileSource>()))
            .ReturnsAsync(new AudioMetadata
            {
                Duration = TimeSpan.FromSeconds(1),
                Format = "m4b"
            });
        Init(services => services.WithSingleton<IMetadataService>(metadata.Object));
        var audiobookToAdd = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();
        audiobookToAdd.Asin = "B012345678";
        var audiobook = await _audiobookRepository.AddAsync(audiobookToAdd);

        var result = await ScanAsync(audiobook, root);

        var tracked = Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Equal(inside, tracked.Path);
        Assert.DoesNotContain(outside, result.AttributedFiles);
        metadata.Verify(
            service => service.ExtractFileMetadataAsync(
                It.IsAny<MetadataFileSource>()),
            Times.Once);
    }

    [Fact]
    public async Task ScanAsync_StableIdentifierBoundary_ClaimsInsideMetadataMatch()
    {
        var root = FileService.GetTempDirectory("scan-service-inside-metadata");
        var identifierDirectory = Path.Join(root, "Author", "Book B012345678");
        Directory.CreateDirectory(identifierDirectory);
        var identifierFile = await FileService.GetFileAsync(
            identifierDirectory,
            "01.m4b",
            "audio");
        var metadataFile = await FileService.GetFileAsync(
            identifierDirectory,
            "unrelated.m4b",
            "audio");
        var metadata = new Mock<IMetadataService>(MockBehavior.Strict);
        metadata.SetupSequence(service => service.ExtractFileMetadataAsync(
                It.IsAny<MetadataFileSource>()))
            .ReturnsAsync(new AudioMetadata
            {
                Duration = TimeSpan.FromSeconds(1),
                Format = "m4b"
            })
            .ReturnsAsync(new AudioMetadata
            {
                Asin = "B012345678",
                Title = "Book",
                Artist = "Author",
                Duration = TimeSpan.FromSeconds(1),
                Format = "m4b"
            });
        Init(services => services.WithSingleton<IMetadataService>(metadata.Object));
        var audiobookToAdd = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();
        audiobookToAdd.Asin = "B012345678";
        var audiobook = await _audiobookRepository.AddAsync(audiobookToAdd);

        var result = await ScanAsync(audiobook, root);

        Assert.Equal(
            [identifierFile, metadataFile],
            result.AttributedFiles.OrderBy(path => path).ToArray());
        Assert.Equal(
            2,
            (await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id)).Count);
        Assert.Equal(identifierDirectory, result.BasePath);
    }

    [Fact]
    public async Task ScanAsync_MetadataReplacementAndRestore_ReadsPinnedFileGeneration()
    {
        var root = FileService.GetTempDirectory("scan-service-metadata-aba");
        var candidate = await FileService.GetFileAsync(
            root,
            "unrelated.m4b",
            "Original Book");
        var displaced = Path.Join(root, "original-generation.bin");
        var metadata = new Mock<IMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.ExtractFileMetadataAsync(
                It.IsAny<string>()))
            .Returns<string>(async extractionPath =>
            {
                var displacedOriginal = false;
                try
                {
                    try
                    {
                        File.Move(candidate, displaced);
                        displacedOriginal = true;
                        await File.WriteAllTextAsync(candidate, "Requested Book");
                    }
                    catch (IOException)
                    {
                        // Windows holds a no-delete-share handle during extraction.
                    }

                    var observed = await File.ReadAllTextAsync(extractionPath);
                    return new AudioMetadata
                    {
                        Title = observed,
                        Duration = TimeSpan.FromSeconds(1),
                        Format = "m4b"
                    };
                }
                finally
                {
                    if (displacedOriginal)
                    {
                        File.Delete(candidate);
                        File.Move(displaced, candidate);
                    }
                }
            });
        Init(services => services.WithSingleton<IMetadataService>(metadata.Object));
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Requested Book")
                .Build());

        var result = await ScanAsync(audiobook, root);

        Assert.Empty(result.AttributedFiles);
        Assert.Empty(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Equal("Original Book", await File.ReadAllTextAsync(candidate));
        metadata.Verify(
            service => service.ExtractFileMetadataAsync(It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task ScanAsync_AttributedFileReplacedBeforeRegistration_NeverClaimsReplacementGeneration()
    {
        var root = FileService.GetTempDirectory("scan-service-claim-generation");
        var candidate = await FileService.GetFileAsync(
            root,
            "Requested Book.m4b",
            "original-generation");
        var displaced = Path.Join(root, "original-generation.displaced");
        var replacementSucceeded = false;
        var metadata = new Mock<IMetadataService>(MockBehavior.Strict);
        metadata.Setup(service => service.ExtractFileMetadataAsync(
                It.IsAny<MetadataFileSource>()))
            .Returns<MetadataFileSource>(async fileSource =>
            {
                try
                {
                    File.Move(candidate, displaced);
                    await File.WriteAllTextAsync(candidate, "replacement-generation");
                    replacementSucceeded = true;
                }
                catch (IOException)
                {
                    // Windows stable-registration handles deny delete and rename sharing.
                }

                var observed = await File.ReadAllTextAsync(
                    fileSource.ReadPath);
                return new AudioMetadata
                {
                    Title = "Requested Book",
                    Duration = TimeSpan.FromSeconds(1),
                    Format = observed
                };
            });
        Init(services => services.WithSingleton<IMetadataService>(metadata.Object));
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Requested Book")
                .WithBasePath(root)
                .Build());

        await ScanAsync(audiobook, root);

        var tracked = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
        Assert.DoesNotContain(
            tracked,
            file => string.Equals(
                file.Format,
                "replacement-generation",
                StringComparison.Ordinal));
        if (replacementSucceeded)
        {
            Assert.Empty(tracked);
        }
        else
        {
            var claimed = Assert.Single(tracked);
            Assert.Equal("original-generation", claimed.Format);
            var physicalIdentityProperty = Assert.IsAssignableFrom<System.Reflection.PropertyInfo>(
                typeof(AudiobookFile).GetProperty("PhysicalObjectIdentity"));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsType<string>(physicalIdentityProperty.GetValue(claimed))));
        }
    }

    [Fact]
    public async Task ScanAsync_TrackedPathReplaced_ReconcilesPhysicalGeneration()
    {
        var root = FileService.GetTempDirectory("scan-service-tracked-replacement");
        var candidate = await FileService.GetFileAsync(
            root,
            "Requested Book.m4b",
            "original-generation");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Requested Book")
                .WithBasePath(root)
                .Build());

        var initialResult = await ScanAsync(audiobook, root);
        Assert.Equal(1, initialResult.CreatedCount);
        var original = Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.False(string.IsNullOrWhiteSpace(original.PhysicalObjectIdentity));
        var originalIdentity = original.PhysicalObjectIdentity;
        var displaced = Path.Join(
            Path.GetDirectoryName(root)!,
            $"original-generation-{Guid.NewGuid():N}.m4b");
        File.Move(candidate, displaced);
        await File.WriteAllTextAsync(candidate, "replacement-generation");

        var replacementResult = await ScanAsync(audiobook, root);

        var replacement = Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Equal(original.Id, replacement.Id);
        Assert.NotEqual(originalIdentity, replacement.PhysicalObjectIdentity);
        Assert.Equal(candidate, replacement.Path);
        Assert.Equal(0, replacementResult.CreatedCount);
        Assert.Empty(replacementResult.RemovedFiles);
        Assert.Contains(
            replacementResult.Diagnostics,
            diagnostic => diagnostic.Code == "TrackedFileGenerationReplaced");
    }

    [Fact]
    public async Task ScanAsync_StableIdentifierBoundary_PreservesExistingOwnedOutsideFileWithoutWideningBasePath()
    {
        var root = FileService.GetTempDirectory("scan-service-owned-outside");
        var identifierDirectory = Path.Join(root, "Author", "Book B012345678");
        var legacyDirectory = Path.Join(root, "Author", "Legacy");
        Directory.CreateDirectory(identifierDirectory);
        Directory.CreateDirectory(legacyDirectory);
        var inside = await FileService.GetFileAsync(identifierDirectory, "01.m4b", "audio");
        var outside = await FileService.GetFileAsync(legacyDirectory, "legacy.m4b", "audio");
        var audiobookToAdd = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .WithBasePath(identifierDirectory)
            .Build();
        audiobookToAdd.Asin = "B012345678";
        var audiobook = await _audiobookRepository.AddAsync(audiobookToAdd);
        await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(outside)
            .Build());

        var result = await ScanAsync(audiobook, root);

        Assert.Equal(identifierDirectory, result.BasePath);
        Assert.Equal(
            [inside, outside],
            result.AttributedFiles.OrderBy(path => path).ToArray());
        Assert.Equal(
            2,
            (await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id)).Count);
    }

    [Fact]
    public async Task ScanAsync_SameAuthorSiblingBooks_ClaimsOnlyRequestedBook()
    {
        var root = FileService.GetTempDirectory("shared-scan-service");
        var requestedDirectory = Path.Join(root, "Shared Author", "Book One");
        var siblingDirectory = Path.Join(root, "Shared Author", "Book Two");
        Directory.CreateDirectory(requestedDirectory);
        Directory.CreateDirectory(siblingDirectory);
        var requestedFile = await FileService.GetFileAsync(
            requestedDirectory,
            "Book One.m4b",
            "audio");
        _ = await FileService.GetFileAsync(
            siblingDirectory,
            "Book Two.m4b",
            "audio");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Book One")
            .WithAuthor("Shared Author")
            .Build());

        var result = await ScanAsync(audiobook, root);

        var tracked = Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Equal(requestedFile, tracked.Path);
        Assert.Equal(requestedDirectory, result.Audiobook.BasePath);
        Assert.DoesNotContain(
            result.AttributedFiles,
            path => FileSystemPathIdentity.IsSameOrInside(
                path,
                siblingDirectory,
                FileSystemPathSemantics.CurrentHostDefault));
    }

    [Fact]
    public async Task ScanAsync_CompleteScan_RemovesOnlyVerifiedMissingRow()
    {
        var root = FileService.GetTempDirectory("scan-service-missing");
        var missingPath = Path.Join(root, "Missing Book.m4b");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Missing Book")
            .WithBasePath(root)
            .Build());
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(missingPath)
            .Build();
        await _audiobookFileRepository.AddAsync(tracked);

        var result = await ScanAsync(audiobook, root);

        Assert.True(result.IsComplete);
        Assert.True(result.ReconciliationPerformed);
        var removed = Assert.Single(result.RemovedFiles);
        Assert.Equal(tracked.Id, removed.Id);
        Assert.Empty(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        var history = await _historyRepository.GetByAudiobookIdAsync(audiobook.Id);
        Assert.Contains(history, entry =>
            entry.EventType == "File Removed"
            && entry.Message != null
            && entry.Message.Contains("Verified missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_FocusedScope_PreservesMissingRowOutsideScope()
    {
        var root = FileService.GetTempDirectory("scan-service-focused-root");
        var focused = Path.Join(root, "Book", "CD1");
        var outsideMissing = Path.Join(root, "Book", "CD2", "02.mp3");
        Directory.CreateDirectory(focused);
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Book")
            .WithBasePath(Path.Join(root, "Book"))
            .Build());
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(outsideMissing)
            .Build();
        await _audiobookFileRepository.AddAsync(tracked);

        var result = await ScanAsync(
            audiobook,
            focused,
            isAuthoritativeScope: false);

        Assert.False(result.ReconciliationPerformed);
        Assert.Empty(result.RemovedFiles);
        Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
    }

    [Fact]
    public async Task ScanAsync_ExistingUnattributedLegacyPath_IsNotClaimed()
    {
        var root = FileService.GetTempDirectory("scan-service-legacy");
        var foreignPath = await FileService.GetFileAsync(
            root,
            "Another Book.m4b",
            "audio");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Requested Book")
            .WithBasePath(root)
            .WithFilePath(foreignPath)
            .Build());

        var result = await ScanAsync(audiobook, root);

        Assert.Empty(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Equal(foreignPath, result.Audiobook.FilePath);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "LegacyPathNotAttributed");
    }

    [Fact]
    public async Task ScanAsync_RootDisappearsAfterDiscovery_PreservesTrackedRows()
    {
        var root = FileService.GetTempDirectory("scan-service-root-disappears");
        var missingPath = Path.Join(root, "Missing Book.m4b");
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.SetupSequence(system => system.DirectoryExists(root))
            .Returns(true)
            .Returns(false);
        fileSystem.Setup(system => system.IsReparsePoint(root)).Returns(false);
        fileSystem.Setup(system => system.EnumerateFiles(root)).Returns([]);
        fileSystem.Setup(system => system.EnumerateDirectories(root)).Returns([]);
        _services.AddSingleton(fileSystem.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Missing Book")
            .WithBasePath(root)
            .Build());
        await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(missingPath)
            .Build());

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            ScanAsync(audiobook, root));

        Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
    }

    [Fact]
    public async Task ScanAsync_ConfiguredAuthorityChangesDuringDiscovery_DoesNotMutate()
    {
        var root = FileService.GetTempDirectory("scan-service-authority-race");
        _ = await FileService.GetFileAsync(root, "Race Book.m4b", "audio");
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithOutputPath(FileService.GetTempPath())
                .Build());
        var initialAuthorization = await _provider
            .GetRequiredService<IScanPathAuthorizationService>()
            .AuthorizeAsync(root);
        Assert.True(initialAuthorization.IsAuthorized, initialAuthorization.Error);
        var identity = Assert.IsType<PathIdentitySnapshot>(
            initialAuthorization.Identity);
        var physicalIdentity = Assert.IsType<ScanPathPhysicalIdentity>(
            initialAuthorization.PhysicalIdentity);
        var authorization = new Mock<IScanPathAuthorizationService>(
            MockBehavior.Strict);
        authorization.SetupSequence(service => service.AuthorizeAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScanPathAuthorizationResult.Authorized(
                root,
                identity,
                physicalIdentity))
            .ReturnsAsync(ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.OutsideConfiguredRoots,
                "root changed"));
        _services.AddSingleton(authorization.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Race Book")
                .Build());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.GetRequiredService<IAudiobookScanService>()
                .ScanAsync(new AudiobookScanCommand(
                    audiobook.Id,
                    root,
                    identity,
                    physicalIdentity)));

        Assert.Contains("root changed", exception.Message);
        Assert.Empty(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        var persisted = Assert.IsType<Audiobook>(
            await _audiobookRepository.GetByIdSnapshotAsync(audiobook.Id));
        Assert.Null(persisted.BasePath);
    }

    [Fact]
    public async Task ScanAsync_PhysicalAuthorityChangesDuringDiscovery_DoesNotMutate()
    {
        var root = FileService.GetTempDirectory("scan-service-physical-authority-race");
        _ = await FileService.GetFileAsync(root, "Physical Race Book.m4b", "audio");
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithOutputPath(FileService.GetTempPath())
                .Build());
        var initialAuthorization = await _provider
            .GetRequiredService<IScanPathAuthorizationService>()
            .AuthorizeAsync(root);
        Assert.True(initialAuthorization.IsAuthorized, initialAuthorization.Error);
        var identity = Assert.IsType<PathIdentitySnapshot>(
            initialAuthorization.Identity);
        var originalPhysical = Assert.IsType<ScanPathPhysicalIdentity>(
            initialAuthorization.PhysicalIdentity);
        var replacementPhysical = originalPhysical with
        {
            ScanRootObjectIdentity =
                $"replacement:{originalPhysical.ScanRootObjectIdentity}"
        };
        var authorization = new Mock<IScanPathAuthorizationService>(
            MockBehavior.Strict);
        authorization.SetupSequence(service => service.AuthorizeAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScanPathAuthorizationResult.Authorized(
                root,
                identity,
                originalPhysical))
            .ReturnsAsync(ScanPathAuthorizationResult.Authorized(
                root,
                identity,
                replacementPhysical));
        _services.AddSingleton(authorization.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Physical Race Book")
                .Build());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.GetRequiredService<IAudiobookScanService>()
                .ScanAsync(new AudiobookScanCommand(
                    audiobook.Id,
                    root,
                    identity,
                    originalPhysical)));

        Assert.Contains("physical", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        var persisted = Assert.IsType<Audiobook>(
            await _audiobookRepository.GetByIdSnapshotAsync(audiobook.Id));
        Assert.Null(persisted.BasePath);
    }

    [Fact]
    public async Task ScanAsync_RootReplacedAndRestoredDuringEnumeration_DoesNotMutate()
    {
        var parent = FileService.GetTempDirectory(
            "scan-service-root-aba-parent");
        var root = Path.Join(parent, "library");
        var displaced = Path.Join(parent, "library-displaced");
        Directory.CreateDirectory(root);
        var missingPath = Path.Join(root, "Missing Book.m4b");
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithOutputPath(root)
                .Build());
        var authorization = await _provider
            .GetRequiredService<IScanPathAuthorizationService>()
            .AuthorizeAsync(root);
        Assert.True(authorization.IsAuthorized, authorization.Error);
        var stableAuthorization = new Mock<IScanPathAuthorizationService>(
            MockBehavior.Strict);
        stableAuthorization.Setup(service => service.AuthorizeAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(authorization);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(system => system.DirectoryExists(root)).Returns(true);
        fileSystem.Setup(system => system.IsReparsePoint(It.IsAny<string>()))
            .Returns(false);
        fileSystem.Setup(system => system.EnumerateFiles(root))
            .Returns(() =>
            {
                Directory.Move(root, displaced);
                try
                {
                    Directory.CreateDirectory(root);
                    File.WriteAllText(
                        Path.Join(root, "Missing Book.m4b"),
                        "replacement audio");
                    return Directory.EnumerateFiles(root).ToArray();
                }
                finally
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }

                    Directory.Move(displaced, root);
                }
            });
        fileSystem.Setup(system => system.EnumerateDirectories(root))
            .Returns([]);
        _services.AddSingleton(stableAuthorization.Object);
        _services.AddSingleton(fileSystem.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Missing Book")
                .WithBasePath(root)
                .Build());
        await _audiobookFileRepository.AddAsync(
            new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(missingPath)
                .Build());

        var pathIdentity = Assert.IsType<PathIdentitySnapshot>(
            authorization.Identity);
        var physicalIdentity = Assert.IsType<ScanPathPhysicalIdentity>(
            authorization.PhysicalIdentity);
        var result = await _provider
            .GetRequiredService<IAudiobookScanService>()
            .ScanAsync(new AudiobookScanCommand(
                audiobook.Id,
                root,
                pathIdentity,
                physicalIdentity));

        Assert.False(result.IsComplete);
        Assert.False(result.ReconciliationPerformed);
        Assert.Empty(result.AttributedFiles);
        Assert.Empty(result.RemovedFiles);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ReconciliationSkippedIncompleteScan");
        Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.False(File.Exists(missingPath));
    }

    [Fact]
    public async Task ScanAsync_NewClaimFails_WithOnlyMissingLegacyRow_RollsBackBasePath()
    {
        var root = FileService.GetTempDirectory(
            "scan-service-failed-claim-basepath");
        var bookDirectory = Path.Join(root, "Author", "New Book");
        Directory.CreateDirectory(bookDirectory);
        _ = await FileService.GetFileAsync(
            bookDirectory,
            "New Book.m4b",
            "audio");
        var fileService = new Mock<IAudiobookFileService>(
            MockBehavior.Strict);
        fileService.Setup(service => service.EnsureAudiobookFileAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<IAudiobookFileRegistrationLease>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _services.AddSingleton(fileService.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("New Book")
                .WithAuthor("Author")
                .WithBasePath(root)
                .Build());
        await _audiobookFileRepository.AddAsync(
            new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(Path.Join(root, "Legacy", "missing.m4b"))
                .Build());

        var result = await ScanAsync(audiobook, root);

        Assert.Equal(root, result.Audiobook.BasePath);
        Assert.Equal(root, result.BasePath);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "BasePathRolledBack");
        fileService.Verify(service => service.EnsureAudiobookFileAsync(
            It.Is<Audiobook>(candidate => candidate.Id == audiobook.Id),
            It.IsAny<IAudiobookFileRegistrationLease>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScanAsync_HistoryFailureAfterVerifiedRemoval_DoesNotReverseSuccess()
    {
        var root = FileService.GetTempDirectory(
            "scan-service-history-failure");
        var missingPath = Path.Join(root, "Book", "missing.m4b");
        var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
        history.Setup(repository => repository.AddAsync(
                It.IsAny<History>(),
                CancellationToken.None))
            .ThrowsAsync(new IOException("simulated history failure"));
        _services.AddSingleton(history.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book")
                .WithBasePath(root)
                .Build());
        var tracked = await _audiobookFileRepository.AddAsync(
            new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(missingPath)
                .Build());

        var result = await ScanAsync(audiobook, root);

        Assert.Contains(result.RemovedFiles, removed =>
            removed.Id == tracked.Id);
        Assert.Empty(await _audiobookFileRepository
            .GetByAudiobookIdAsync(audiobook.Id));
        history.Verify(repository => repository.AddAsync(
            It.Is<History>(entry =>
                entry.AudiobookId == audiobook.Id
                && entry.EventType == "File Removed"),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ScanAsync_IncompleteEnumeration_PreservesMissingTrackedRows()
    {
        var root = FileService.GetTempDirectory("scan-service-incomplete");
        var failingDirectory = Path.Join(root, "Book");
        Directory.CreateDirectory(failingDirectory);
        var missingPath = Path.Join(failingDirectory, "Missing Book.m4b");
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(system => system.DirectoryExists(root)).Returns(true);
        fileSystem.Setup(system => system.IsReparsePoint(It.IsAny<string>())).Returns(false);
        fileSystem.Setup(system => system.EnumerateFiles(root)).Returns([]);
        fileSystem.Setup(system => system.EnumerateDirectories(root))
            .Returns([failingDirectory]);
        fileSystem.Setup(system => system.EnumerateFiles(failingDirectory))
            .Throws(new UnauthorizedAccessException("denied"));
        _services.AddSingleton(fileSystem.Object);
        Init();
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Missing Book")
            .WithBasePath(root)
            .Build());
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(missingPath)
            .Build();
        await _audiobookFileRepository.AddAsync(tracked);

        var result = await ScanAsync(audiobook, root);

        Assert.False(result.IsComplete);
        Assert.False(result.ReconciliationPerformed);
        Assert.Empty(result.RemovedFiles);
        Assert.Single(
            await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "ReconciliationSkippedIncompleteScan");
    }

    private async Task<AudiobookScanResult> ScanAsync(
        Audiobook audiobook,
        string scanRoot,
        bool isAuthoritativeScope = true)
    {
        var settings = await _applicationSettingsRepository.GetAsync()
            ?? await _applicationSettingsRepository.InitializeIfMissingAsync(
                new ApplicationSettingsBuilder().Build());
        settings.OutputPath = scanRoot;
        await _applicationSettingsRepository.SaveAsync(settings);
        var authorization = await _provider
            .GetRequiredService<IScanPathAuthorizationService>()
            .AuthorizeAsync(scanRoot);
        Assert.True(authorization.IsAuthorized, authorization.Error);
        var pathIdentity = Assert.IsType<PathIdentitySnapshot>(
            authorization.Identity);
        var physicalIdentity = Assert.IsType<ScanPathPhysicalIdentity>(
            authorization.PhysicalIdentity);
        return await _provider.GetRequiredService<IAudiobookScanService>()
            .ScanAsync(new AudiobookScanCommand(
                audiobook.Id,
                scanRoot,
                pathIdentity,
                physicalIdentity,
                IsAuthoritativeScope: isAuthoritativeScope));
    }
}
