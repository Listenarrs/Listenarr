using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Moving;

[Trait("Name", "AudiobookDestinationRewriteServiceTests")]
[Trait("Category", "Application")]
public sealed class AudiobookDestinationRewriteServiceTests : BaseTests
{
    [WindowsFact]
    public async Task RewriteDestinationAsync_PersistedUnixRoot_DoesNotAuthorizeCurrentWindowsDrive()
    {
        var currentDriveRoot = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var destinationPath = Path.Join(
            currentDriveRoot,
            "listenarr-foreign-configured-root",
            Guid.NewGuid().ToString("N"));
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var settings = new Mock<IConfigurationService>(MockBehavior.Strict);
        var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);

        settings.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings { OutputPath = "/" });
        rootFolders.Setup(service => service.GetAllAsync())
            .ReturnsAsync([]);
        semanticsResolver.Setup(service => service.ResolveAsync(
                currentDriveRoot,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    FileSystemPathSemantics.CurrentHostDefault,
                    PathIdentityState.Unavailable,
                    currentDriveRoot,
                    "test")));

        using var operationCoordinator = new AudiobookOperationCoordinator();
        var service = new AudiobookDestinationRewriteService(
            repository.Object,
            settings.Object,
            rootFolders.Object,
            fileSystem.Object,
            semanticsResolver.Object,
            Mock.Of<ILogger<AudiobookDestinationRewriteService>>(),
            relocationService.Object,
            new FilesystemMutationCoordinator(),
            operationCoordinator);

        var exception = await Assert.ThrowsAsync<ApplicationValidationException>(() =>
            service.RewriteDestinationAsync(85, destinationPath, expectedSourcePath: null));

        Assert.Equal("destination_path_outside_roots", exception.Code);
        semanticsResolver.Verify(service => service.ResolveAsync(
            It.IsAny<string>(),
            It.IsAny<FileSystemCaseSensitivityMode>(),
            It.IsAny<CancellationToken>()), Times.Never);
        fileSystem.Verify(service => service.TryValidateMutationTarget(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string?>>(),
            out It.Ref<string>.IsAny,
            out It.Ref<string>.IsAny), Times.Never);
    }

    [WindowsFact]
    public async Task RewriteDestinationAsync_ForeignExpectedSourceAlias_DoesNotMatchNativeCurrentSource()
    {
        var rootPath = FileService.GetWindowsRootRelativeTempDirectory(
            "destination-rewrite-foreign-expected");
        var sourcePath = Path.Join(rootPath, "Author", "Old Title");
        var destinationPath = Path.Join(rootPath, "Author", "New Title");
        var foreignExpectedSource = TempFileService
            .GetWindowsRootRelativeForeignAlias(sourcePath);

        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var settings = new Mock<IConfigurationService>(MockBehavior.Strict);
        var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        var semantics = FileSystemPathSemantics.CurrentHostDefault;

        settings.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings { OutputPath = rootPath });
        rootFolders.Setup(service => service.GetAllAsync())
            .ReturnsAsync([]);
        semanticsResolver.Setup(service => service.ResolveAsync(
                rootPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(semantics, PathIdentityState.Valid, rootPath)));
        string normalizedTarget = destinationPath;
        string validationReason = string.Empty;
        fileSystem.Setup(service => service.TryValidateMutationTarget(
                destinationPath,
                It.IsAny<IEnumerable<string?>>(),
                out normalizedTarget,
                out validationReason))
            .Returns(true);
        repository.Setup(repo => repo.GetByIdAsync(87))
            .ReturnsAsync(new Audiobook
            {
                Id = 87,
                Title = "Old Title",
                BasePath = sourcePath
            });
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(repo => repo.RewritePathReferencesAsync(
                87,
                sourcePath,
                destinationPath,
                semantics,
                semantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var operationCoordinator = new AudiobookOperationCoordinator();
        var service = new AudiobookDestinationRewriteService(
            repository.Object,
            settings.Object,
            rootFolders.Object,
            fileSystem.Object,
            semanticsResolver.Object,
            Mock.Of<ILogger<AudiobookDestinationRewriteService>>(),
            relocationService.Object,
            new FilesystemMutationCoordinator(),
            operationCoordinator);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.RewriteDestinationAsync(
                87,
                destinationPath,
                expectedSourcePath: foreignExpectedSource));

        Assert.Equal("source_path_changed", exception.Code);
        repository.Verify(repo => repo.RewritePathReferencesAsync(
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<FileSystemPathSemantics>(),
            It.IsAny<FileSystemPathSemantics>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<FileSystemCaseSensitivityMode>()), Times.Never);
    }

    [Fact]
    public async Task RewriteDestinationAsync_SourceOutsideConfiguredRoots_DoesNotUseTargetSemanticsForStaleToken()
    {
        var rootPath = Path.Join(
            Path.GetTempPath(),
            $"listenarr-rewrite-insensitive-target-{Guid.NewGuid():N}");
        var destinationPath = Path.Join(rootPath, "Author", "New Title");
        var sourcePath = OperatingSystem.IsWindows()
            ? @"Z:\Legacy\Author\Book"
            : "/legacy/Author/Book";
        var staleExpectedSource = OperatingSystem.IsWindows()
            ? @"Z:\Legacy\Author\book"
            : "/legacy/Author/book";
        var targetSemantics = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Insensitive);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var settings = new Mock<IConfigurationService>(MockBehavior.Strict);
        var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);

        settings.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings { OutputPath = rootPath });
        rootFolders.Setup(service => service.GetAllAsync())
            .ReturnsAsync([]);
        semanticsResolver.Setup(service => service.ResolveAsync(
                rootPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(
                    targetSemantics,
                    PathIdentityState.Valid,
                    rootPath)));
        string normalizedTarget = destinationPath;
        string validationReason = string.Empty;
        fileSystem.Setup(service => service.TryValidateMutationTarget(
                destinationPath,
                It.IsAny<IEnumerable<string?>>(),
                out normalizedTarget,
                out validationReason))
            .Returns(true);
        repository.Setup(repo => repo.GetByIdAsync(88))
            .ReturnsAsync(new Audiobook
            {
                Id = 88,
                Title = "Book",
                BasePath = sourcePath
            });
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(repo => repo.RewritePathReferencesAsync(
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FileSystemCaseSensitivityMode>()))
            .ReturnsAsync(true);

        using var operationCoordinator = new AudiobookOperationCoordinator();
        var service = new AudiobookDestinationRewriteService(
            repository.Object,
            settings.Object,
            rootFolders.Object,
            fileSystem.Object,
            semanticsResolver.Object,
            Mock.Of<ILogger<AudiobookDestinationRewriteService>>(),
            relocationService.Object,
            new FilesystemMutationCoordinator(),
            operationCoordinator);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.RewriteDestinationAsync(
                88,
                destinationPath,
                expectedSourcePath: staleExpectedSource));

        Assert.Equal("source_path_changed", exception.Code);
        repository.Verify(repo => repo.RewritePathReferencesAsync(
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<FileSystemPathSemantics>(),
            It.IsAny<FileSystemPathSemantics>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<FileSystemCaseSensitivityMode>()), Times.Never);
    }

    [Fact]
    public async Task RewriteDestinationAsync_RepairsLegacyInvalidBasePathWhenExpectedSourceMatchesExactly()
    {
        var rootPath = Path.Join(Path.GetTempPath(), $"listenarr-root-{Guid.NewGuid():N}");
        var destinationPath = Path.Join(rootPath, "Author", "Valid Title");
        const string legacyInvalidSourcePath = "\0invalid";
        var normalizedLegacySourcePath = FileUtils.NormalizeStoredPath(legacyInvalidSourcePath);
        var conservativeSourceSemantics = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Sensitive);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var settings = new Mock<IConfigurationService>(MockBehavior.Strict);
        var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        var rootSemantics = FileSystemPathSemantics.CurrentHostDefault;

        settings.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings { OutputPath = rootPath });
        rootFolders.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Library",
                    Path = rootPath,
                    IsDefault = true,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
                }
            ]);
        semanticsResolver.Setup(service => service.ResolveAsync(
                rootPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(rootSemantics, PathIdentityState.Valid, rootPath)));
        string normalizedTarget = destinationPath;
        string validationReason = string.Empty;
        fileSystem.Setup(service => service.TryValidateMutationTarget(
                destinationPath,
                It.IsAny<IEnumerable<string?>>(),
                out normalizedTarget,
                out validationReason))
            .Returns(true);
        repository.Setup(repo => repo.GetByIdAsync(85))
            .ReturnsAsync(new Audiobook
            {
                Id = 85,
                Title = "Legacy Title",
                BasePath = legacyInvalidSourcePath
            });
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(repo => repo.RewritePathReferencesAsync(
                85,
                normalizedLegacySourcePath,
                destinationPath,
                conservativeSourceSemantics,
                rootSemantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var operationCoordinator = new AudiobookOperationCoordinator();
        var service = new AudiobookDestinationRewriteService(
            repository.Object,
            settings.Object,
            rootFolders.Object,
            fileSystem.Object,
            semanticsResolver.Object,
            Mock.Of<ILogger<AudiobookDestinationRewriteService>>(),
            relocationService.Object,
            new FilesystemMutationCoordinator(),
            operationCoordinator);

        var result = await service.RewriteDestinationAsync(
            85,
            destinationPath,
            expectedSourcePath: legacyInvalidSourcePath);

        Assert.Equal(destinationPath, result.DestinationPath);
        Assert.Equal(normalizedLegacySourcePath, result.SourcePath);
        fileSystem.Verify(service => service.DirectoryExists(It.IsAny<string>()), Times.Never);
        semanticsResolver.Verify(service => service.ResolveAsync(
            rootPath,
            FileSystemCaseSensitivityMode.Auto,
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RewriteDestinationAsync_NestedRootUsesMostSpecificSemantics()
    {
        var outerRoot = Path.Join(Path.GetTempPath(), $"listenarr-outer-{Guid.NewGuid():N}");
        var innerRoot = Path.Join(outerRoot, "Sensitive Library");
        var sourcePath = Path.Join(innerRoot, "Author", "Old Title");
        var destinationPath = Path.Join(innerRoot, "Author", "New Title");
        var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;
        var outerSemantics = new FileSystemPathSemantics(
            syntax,
            FileSystemCaseSensitivity.Insensitive);
        var innerSemantics = new FileSystemPathSemantics(
            syntax,
            FileSystemCaseSensitivity.Sensitive);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var settings = new Mock<IConfigurationService>(MockBehavior.Strict);
        var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);

        settings.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings { OutputPath = outerRoot });
        rootFolders.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "A Outer",
                    Path = outerRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                },
                new RootFolder
                {
                    Id = 2,
                    Name = "Z Inner",
                    Path = innerRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
                }
            ]);
        semanticsResolver.Setup(service => service.ResolveAsync(
                outerRoot,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(outerSemantics, PathIdentityState.Valid, outerRoot)));
        semanticsResolver.Setup(service => service.ResolveAsync(
                outerRoot,
                FileSystemCaseSensitivityMode.Insensitive,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(outerSemantics, PathIdentityState.Valid, outerRoot)));
        semanticsResolver.Setup(service => service.ResolveAsync(
                innerRoot,
                FileSystemCaseSensitivityMode.Sensitive,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(innerSemantics, PathIdentityState.Valid, innerRoot)));
        string normalizedTarget = destinationPath;
        string validationReason = string.Empty;
        fileSystem.Setup(service => service.TryValidateMutationTarget(
                destinationPath,
                It.IsAny<IEnumerable<string?>>(),
                out normalizedTarget,
                out validationReason))
            .Returns(true);
        repository.Setup(repo => repo.GetByIdAsync(86))
            .ReturnsAsync(new Audiobook
            {
                Id = 86,
                Title = "Old Title",
                BasePath = sourcePath
            });
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(repo => repo.RewritePathReferencesAsync(
                86,
                sourcePath,
                destinationPath,
                innerSemantics,
                innerSemantics,
                It.IsAny<CancellationToken>(),
                FileSystemCaseSensitivityMode.Sensitive))
            .ReturnsAsync(true);

        using var operationCoordinator = new AudiobookOperationCoordinator();
        var service = new AudiobookDestinationRewriteService(
            repository.Object,
            settings.Object,
            rootFolders.Object,
            fileSystem.Object,
            semanticsResolver.Object,
            Mock.Of<ILogger<AudiobookDestinationRewriteService>>(),
            relocationService.Object,
            new FilesystemMutationCoordinator(),
            operationCoordinator);

        var result = await service.RewriteDestinationAsync(86, destinationPath, sourcePath);

        Assert.Equal(destinationPath, result.DestinationPath);
        repository.VerifyAll();
    }

    [Fact]
    public async Task RewriteDestinationAsync_UpdatesMetadataWithoutSourceFilesystemAccess()
    {
        var rootPath = Path.Join(Path.GetTempPath(), $"listenarr-root-{Guid.NewGuid():N}");
        var sourcePath = Path.Join(rootPath, "Author", "Old Title");
        var destinationPath = Path.Join(rootPath, "Author", "Missing Title");
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        var settings = new Mock<IConfigurationService>(MockBehavior.Strict);
        var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var relocationService = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        var rootSemantics = FileSystemPathSemantics.CurrentHostDefault;

        settings.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings { OutputPath = rootPath });
        rootFolders.Setup(service => service.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Library",
                    Path = rootPath,
                    IsDefault = true,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
                }
            ]);
        semanticsResolver.Setup(service => service.ResolveAsync(
                rootPath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<FileSystemSemanticsResolution>(
                new FileSystemSemanticsResolution(rootSemantics, PathIdentityState.Valid, rootPath)));
        string normalizedTarget = destinationPath;
        string validationReason = string.Empty;
        fileSystem.Setup(service => service.TryValidateMutationTarget(
                destinationPath,
                It.IsAny<IEnumerable<string?>>(),
                out normalizedTarget,
                out validationReason))
            .Returns(true);
        repository.Setup(repo => repo.GetByIdAsync(85))
            .ReturnsAsync(new Audiobook
            {
                Id = 85,
                Title = "Old Title",
                BasePath = sourcePath
            });
        relocationService.Setup(service => service.IsBoundaryProtectedAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemPathSemantics>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository.Setup(repo => repo.RewritePathReferencesAsync(
                85,
                sourcePath,
                destinationPath,
                rootSemantics,
                rootSemantics,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var operationCoordinator = new AudiobookOperationCoordinator();
        var service = new AudiobookDestinationRewriteService(
            repository.Object,
            settings.Object,
            rootFolders.Object,
            fileSystem.Object,
            semanticsResolver.Object,
            Mock.Of<ILogger<AudiobookDestinationRewriteService>>(),
            relocationService.Object,
            new FilesystemMutationCoordinator(),
            operationCoordinator);

        var result = await service.RewriteDestinationAsync(85, destinationPath, expectedSourcePath: null);

        Assert.Equal(85, result.AudiobookId);
        Assert.Equal(destinationPath, result.DestinationPath);
        Assert.Equal(sourcePath, result.SourcePath);
        fileSystem.Verify(service => service.DirectoryExists(It.IsAny<string>()), Times.Never);
        semanticsResolver.Verify(service => service.ResolveAsync(
            rootPath,
            FileSystemCaseSensitivityMode.Auto,
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
