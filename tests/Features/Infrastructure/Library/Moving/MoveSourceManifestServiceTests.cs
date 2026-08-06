using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "MoveSourceManifestServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class MoveSourceManifestServiceTests : BaseTests
{
    [Fact]
    public async Task BuildAsync_BroadAuthorBasePath_UsesTrackedBookDirectory()
    {
        var root = FileService.GetTempDirectory("move-manifest-root");
        var author = Path.Join(root, "Shared Author");
        var requestedBook = Path.Join(author, "Book One");
        var siblingBook = Path.Join(author, "Book Two");
        Directory.CreateDirectory(requestedBook);
        Directory.CreateDirectory(siblingBook);
        var requestedFile = await FileService.GetFileAsync(
            requestedBook,
            "Book One.m4b",
            "requested");
        _ = await FileService.GetFileAsync(
            siblingBook,
            "Book Two.m4b",
            "foreign");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book One")
                .WithBasePath(author)
                .Build());
        await AddTrackedFileAsync(audiobook, requestedFile, root);

        var manifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(audiobook);

        Assert.Equal(requestedBook, manifest.SourceRoot);
        var file = Assert.Single(
            manifest.Entries,
            entry => entry.EntryType == MoveJobEntryType.File);
        Assert.Equal("Book One.m4b", file.RelativePath);
        Assert.DoesNotContain(manifest.Entries, entry =>
            entry.RelativePath.Contains("Book Two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildPlanAsync_ProducesStructuralManifestWithoutReadingContentHash()
    {
        var root = FileService.GetTempDirectory("move-plan-metadata-only");
        var filePath = await FileService.GetFileAsync(
            root,
            "Book.m4b",
            "source bytes");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, filePath, root);

        var plan = await _provider
            .GetRequiredService<IMoveSourcePlanService>()
            .BuildPlanAsync(new AudiobookPathReferenceSnapshot(
                audiobook.Id,
                audiobook.BasePath,
                audiobook.FilePath));
        var fullManifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(audiobook);

        var plannedFile = Assert.Single(
            plan.Entries,
            entry => entry.EntryType == MoveJobEntryType.File);
        var hashedFile = Assert.Single(
            fullManifest.Entries,
            entry => entry.EntryType == MoveJobEntryType.File);
        Assert.Null(plannedFile.Sha256);
        Assert.NotNull(hashedFile.Sha256);
        Assert.Equal(hashedFile.RelativePath, plannedFile.RelativePath);
        Assert.Equal(hashedFile.Length, plannedFile.Length);
        Assert.Equal(hashedFile.LastWriteTimeUtc, plannedFile.LastWriteTimeUtc);
    }

    [Fact]
    public async Task BuildAsync_SharedFlatFolder_IncludesOnlyTrackedFile()
    {
        var root = FileService.GetTempDirectory("move-manifest-flat");
        var requestedFile = await FileService.GetFileAsync(
            root,
            "Book One.m4b",
            "requested");
        _ = await FileService.GetFileAsync(
            root,
            "Book Two.m4b",
            "foreign");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book One")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, requestedFile, root);

        var manifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(audiobook);

        Assert.Equal(root, manifest.SourceRoot);
        var file = Assert.Single(manifest.Entries);
        Assert.Equal("Book One.m4b", file.RelativePath);
        Assert.Equal(MoveJobEntryType.File, file.EntryType);
    }

    [Fact]
    public async Task BuildAsync_ManagedAudiobookFolder_IncludesNonAudioCompanionButNotUntrackedAudio()
    {
        var root = FileService.GetTempDirectory("move-manifest-companion-root");
        await AddAuthorizedRootAsync(root);
        var book = Path.Join(root, "Author", "Book");
        Directory.CreateDirectory(book);
        var trackedAudio = await FileService.GetFileAsync(
            book,
            "Book.m4b",
            "tracked audio");
        _ = await FileService.GetFileAsync(
            book,
            "cover.jpg",
            "cover image");
        _ = await FileService.GetFileAsync(
            book,
            "untracked-bonus.m4b",
            "foreign audio");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book")
                .WithBasePath(book)
                .Build());
        // Scans may persist the audiobook directory itself as the file identity
        // boundary rather than the configured library root. Companion ownership
        // must come from the managed-root authorizer, not this path identity field.
        await AddTrackedFileAsync(audiobook, trackedAudio, book);

        var manifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(audiobook);

        Assert.Equal(book, manifest.SourceRoot);
        var files = manifest.Entries
            .Where(entry => entry.EntryType == MoveJobEntryType.File)
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["Book.m4b", "cover.jpg"], files.Select(entry => entry.RelativePath));
        Assert.DoesNotContain(files, entry =>
            string.Equals(entry.RelativePath, "untracked-bonus.m4b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_AudiobookAtConfiguredRoot_DoesNotClaimRootCompanion()
    {
        var root = FileService.GetTempDirectory("move-manifest-root-level-companion");
        await AddAuthorizedRootAsync(root);
        var trackedAudio = await FileService.GetFileAsync(
            root,
            "Book.m4b",
            "tracked audio");
        _ = await FileService.GetFileAsync(
            root,
            "cover.jpg",
            "root-level cover");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Root Book")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, trackedAudio, root);

        var manifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(audiobook);

        var file = Assert.Single(
            manifest.Entries,
            entry => entry.EntryType == MoveJobEntryType.File);
        Assert.Equal("Book.m4b", file.RelativePath);
        Assert.DoesNotContain(manifest.Entries, entry =>
            string.Equals(entry.RelativePath, "cover.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_SharedAudiobookFolder_DoesNotClaimNonAudioCompanion()
    {
        var root = FileService.GetTempDirectory("move-manifest-shared-companion-root");
        await AddAuthorizedRootAsync(root);
        var shared = Path.Join(root, "Shared");
        Directory.CreateDirectory(shared);
        var requestedAudio = await FileService.GetFileAsync(
            shared,
            "Book One.m4b",
            "requested");
        var otherAudio = await FileService.GetFileAsync(
            shared,
            "Book Two.m4b",
            "other");
        _ = await FileService.GetFileAsync(
            shared,
            "cover.jpg",
            "ambiguous cover");
        var requested = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book One")
                .WithBasePath(shared)
                .Build());
        var other = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book Two")
                .WithBasePath(shared)
                .Build());
        await AddTrackedFileAsync(requested, requestedAudio, root);
        await AddTrackedFileAsync(other, otherAudio, root);

        var manifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(requested);

        var file = Assert.Single(
            manifest.Entries,
            entry => entry.EntryType == MoveJobEntryType.File);
        Assert.Equal("Book One.m4b", file.RelativePath);
        Assert.DoesNotContain(manifest.Entries, entry =>
            string.Equals(entry.RelativePath, "cover.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_NestedDiscs_UsesCommonBookDirectory()
    {
        var root = FileService.GetTempDirectory("move-manifest-discs");
        var book = Path.Join(root, "Author", "Book");
        var firstDirectory = Path.Join(book, "CD1");
        var secondDirectory = Path.Join(book, "CD2");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = await FileService.GetFileAsync(firstDirectory, "01.mp3", "one");
        var second = await FileService.GetFileAsync(secondDirectory, "02.mp3", "two");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Book")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, first, root);
        await AddTrackedFileAsync(audiobook, second, root);

        var manifest = await _provider
            .GetRequiredService<IMoveSourceManifestService>()
            .BuildAsync(audiobook);

        Assert.Equal(book, manifest.SourceRoot);
        Assert.Equal(
            ["CD1/01.mp3", "CD2/02.mp3"],
            manifest.Entries
                .Where(entry => entry.EntryType == MoveJobEntryType.File)
                .Select(entry => entry.RelativePath.Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(2, manifest.Entries.Count(entry =>
            entry.EntryType == MoveJobEntryType.Directory));
    }

    [Fact]
    public async Task BuildAsync_UnrelatedTrackedDirectories_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-manifest-unrelated");
        var firstDirectory = Path.Join(root, "Book One");
        var secondDirectory = Path.Join(root, "Book Two");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = await FileService.GetFileAsync(firstDirectory, "01.mp3", "one");
        var second = await FileService.GetFileAsync(secondDirectory, "02.mp3", "two");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Ambiguous")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, first, root);
        await AddTrackedFileAsync(audiobook, second, root);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            _provider.GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook));

        Assert.Equal("move_source_unverified", exception.Code);
        Assert.Contains("unrelated source directories", exception.Message);
    }

    [Fact]
    public async Task BuildAsync_NoTrackedFiles_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-manifest-empty");
        _ = await FileService.GetFileAsync(root, "Untracked.m4b", "foreign");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Untracked")
                .WithBasePath(root)
                .Build());

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            _provider.GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook));

        Assert.Equal("move_source_unverified", exception.Code);
        Assert.Contains("no validated tracked files", exception.Message);
    }

    [WindowsFact]
    public async Task BuildAsync_ForeignPersistedUnixPath_IsRejectedBeforeNativeAliasProbe()
    {
        var root = FileService.GetWindowsRootRelativeTempDirectory(
            "move-manifest-foreign-persisted-path");
        var nativePath = await FileService.GetFileAsync(root, "Book.m4b", "audio");
        var foreignRoot = TempFileService
            .GetWindowsRootRelativeForeignAlias(root);
        var foreignPath = TempFileService
            .GetWindowsRootRelativeForeignAlias(nativePath);
        Assert.True(File.Exists(foreignPath));

        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Foreign Persisted Path")
                .WithBasePath(foreignRoot)
                .Build());
        var foreignSemantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var identity = AudiobookFilePathIdentity.CreateValid(
            foreignPath,
            foreignSemantics,
            FileSystemCaseSensitivityMode.Sensitive,
            foreignRoot);
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(foreignPath)
            .Build();
        tracked.ApplyPathIdentity(foreignPath, identity);
        using (var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            Path.GetDirectoryName(nativePath)!,
            createMissing: false))
        using (var file = parent.OpenExistingFileForStableRead(Path.GetFileName(nativePath)))
        {
            tracked.ApplyPhysicalObjectIdentity(
                file.GetObjectIdentity(),
                DateTime.UtcNow);
        }
        await _audiobookFileRepository.AddAsync(tracked);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            _provider.GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook));

        Assert.Equal("move_source_unverified", exception.Code);
        Assert.Contains("current host", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("audio", await File.ReadAllTextAsync(nativePath));
    }

    [Fact]
    public async Task BuildAsync_ReplacedTrackedPhysicalGeneration_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-manifest-replaced-generation");
        var path = await FileService.GetFileAsync(root, "Book.m4b", "original");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Replaced Generation")
                .WithBasePath(root)
                .Build());
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var identity = AudiobookFilePathIdentity.CreateValid(
            path,
            semantics,
            FileSystemCaseSensitivityMode.Auto,
            root);
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(path)
            .Build();
        tracked.ApplyPathIdentity(path, identity);
        using (var parent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(root))
        using (var file = parent.OpenExistingFileForStableRead(Path.GetFileName(path)))
        {
            tracked.ApplyPhysicalObjectIdentity(
                file.GetObjectIdentity(),
                DateTime.UtcNow);
        }
        await _audiobookFileRepository.AddAsync(tracked);

        var displaced = path + ".original";
        File.Move(path, displaced);
        await File.WriteAllTextAsync(path, "replacement");
        using (var parent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(root))
        using (var replacement = parent.OpenExistingFileForStableRead(Path.GetFileName(path)))
        {
            Assert.NotEqual(
                tracked.PhysicalObjectIdentity,
                replacement.GetObjectIdentity());
        }

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            _provider.GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook));

        Assert.Equal("move_source_unverified", exception.Code);
        Assert.Contains("physical", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("replacement", await File.ReadAllTextAsync(path));
        Assert.Equal("original", await File.ReadAllTextAsync(displaced));
    }

    [Fact]
    public async Task BuildAsync_MissingTrackedPhysicalIdentity_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-manifest-missing-physical");
        var path = await FileService.GetFileAsync(root, "Book.m4b", "audio");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Missing Physical Identity")
                .WithBasePath(root)
                .Build());
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var identity = AudiobookFilePathIdentity.CreateValid(
            path,
            semantics,
            FileSystemCaseSensitivityMode.Auto,
            root);
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(path)
            .Build();
        tracked.ApplyPathIdentity(path, identity);
        await _audiobookFileRepository.AddAsync(tracked);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            _provider.GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook));

        Assert.Equal("move_source_unverified", exception.Code);
        Assert.Contains("physical identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task BuildAsync_MissingTrackedFile_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-manifest-missing");
        var missing = Path.Join(root, "Missing.m4b");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Missing")
                .WithBasePath(root)
                .Build());
        await AddTrackedFileAsync(audiobook, missing, root);

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            _provider.GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook));

        Assert.Equal("move_source_unverified", exception.Code);
        Assert.Contains("missing from disk", exception.Message);
    }

    private async Task AddTrackedFileAsync(
        Audiobook audiobook,
        string path,
        string boundary)
    {
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var identity = AudiobookFilePathIdentity.CreateValid(
            path,
            semantics,
            FileSystemCaseSensitivityMode.Auto,
            boundary);
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(path)
            .Build();
        tracked.ApplyPathIdentity(path, identity);
        if (File.Exists(path))
        {
            var parentPath = Path.GetDirectoryName(path)!;
            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            using var file = parent.OpenExistingFileForStableRead(Path.GetFileName(path));
            tracked.ApplyPhysicalObjectIdentity(
                file.GetObjectIdentity(),
                DateTime.UtcNow);
        }
        await _audiobookFileRepository.AddAsync(tracked);
    }
}
