using System.Security.Cryptography;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "AudiobookFilesystemDeleteServiceTrackedFileGateTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookFilesystemDeleteServiceTrackedFileGateTests : BaseTests
{
    private const string MissingFileWarning = "A tracked audiobook file is missing from the audiobook folder";

    [LinuxFact]
    public async Task DeleteAsync_RenamedPartAndMovedInForeignFile_DeletesOnlyLiveTrackedPart()
    {
        var root = await CreateLibraryAsync("delete-gate-renamed");
        var (book, parts) = await AddBookAsync(root, "The Three Musketeers", "Alexandre Dumas", partCount: 2);
        var (_, otherParts) = await AddBookAsync(root, "The Man in the Iron Mask", "Alexandre Dumas", partCount: 1);
        var renamed = Path.Join(Path.GetDirectoryName(parts[1])!, "renamed.m4b");
        var movedIn = Path.Join(Path.GetDirectoryName(parts[0])!, Path.GetFileName(otherParts[0]));
        File.Move(parts[1], renamed);
        File.Move(otherParts[0], movedIn);
        var before = Manifest(root);

        var result = await DeleteAsync(book);

        AssertManifestLosesOnly(root, before, parts[0]);
        Assert.Equal(1, result.DeletedFiles);
        Assert.False(result.DeletedFolder);
        Assert.Contains(result.Warnings, warning => warning.StartsWith(MissingFileWarning, StringComparison.Ordinal));
    }

    [LinuxFact]
    public async Task DeleteAsync_OneTrackedPartMissing_DeletesOnlyLivePartAndKeepsFolder()
    {
        var root = await CreateLibraryAsync("delete-gate-missing");
        var (book, parts) = await AddBookAsync(root, "The Three Musketeers", "Alexandre Dumas", partCount: 2);
        File.Delete(parts[1]);
        var before = Manifest(root);

        var result = await DeleteAsync(book);

        AssertManifestLosesOnly(root, before, parts[0]);
        Assert.True(result.TrackedFileCleanupComplete);
        Assert.False(result.DeletedFolder);
        Assert.Contains(result.Warnings, warning => warning.StartsWith(MissingFileWarning, StringComparison.Ordinal));
    }

    [LinuxFact]
    public async Task DeleteAsync_EveryTrackedPartPresent_DeletesTrackedFilesCompanionsAndFolder()
    {
        var root = await CreateLibraryAsync("delete-gate-clean");
        var (book, parts) = await AddBookAsync(root, "The Three Musketeers", "Alexandre Dumas", partCount: 2);
        var folder = Path.GetDirectoryName(parts[0])!;
        File.WriteAllText(Path.Join(folder, "cover.txt"), "companion");
        var bystander = await AddBookAsync(root, "A Princess of Mars", "Edgar Rice Burroughs", partCount: 1);
        var before = Manifest(root);

        var result = await DeleteAsync(book);

        var after = Manifest(root);
        Assert.Equal(before.Where(entry => !IsWithin(entry.Key, root, folder))
            .ToDictionary(entry => entry.Key, entry => entry.Value), after);
        Assert.Equal(3, result.DeletedFiles);
        Assert.True(result.DeletedFolder, string.Join("; ", result.Warnings));
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith(MissingFileWarning, StringComparison.Ordinal));
        Assert.True(File.Exists(bystander.Parts[0]));
    }

    [LinuxFact]
    public async Task DeleteAsync_OwnedFolderWithTrackedPartMissing_KeepsRecursiveDeleteAndRetiresOwnership()
    {
        var root = await CreateLibraryAsync("delete-gate-owned");
        var (book, parts) = await AddBookAsync(root, "The Three Musketeers", "Alexandre Dumas", partCount: 2);
        var folder = Path.GetDirectoryName(parts[0])!;
        var store = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        await store.RecordCreatedAsync(new LibraryDirectoryOwnershipClaim(
            folder, FileSystemPathSemantics.CurrentHostDefault, "test"));
        File.Move(parts[1], Path.Join(folder, "renamed.m4b"));
        var before = Manifest(root);

        var result = await DeleteAsync(book);

        Assert.Equal(before.Where(entry => !IsWithin(entry.Key, root, folder))
            .ToDictionary(entry => entry.Key, entry => entry.Value), Manifest(root));
        Assert.True(result.DeletedFolder, string.Join("; ", result.Warnings));
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith(MissingFileWarning, StringComparison.Ordinal));
        Assert.Empty(await store.GetOwnedWithinAsync(folder, FileSystemPathSemantics.CurrentHostDefault));
    }

    [LinuxFact]
    public async Task DeleteAsync_EveryTrackedPartMissing_DeletesNothingAndCompletesCleanup()
    {
        var root = await CreateLibraryAsync("delete-gate-all-missing");
        var (book, parts) = await AddBookAsync(root, "The Three Musketeers", "Alexandre Dumas", partCount: 2);
        var folder = Path.GetDirectoryName(parts[0])!;
        File.Delete(parts[0]);
        File.Delete(parts[1]);
        File.WriteAllText(Path.Join(folder, "foreign.m4b"), "foreign");
        var before = Manifest(root);

        var result = await DeleteAsync(book);

        Assert.Equal(before, Manifest(root));
        Assert.Equal(0, result.DeletedFiles);
        Assert.False(result.DeletedFolder);
        Assert.True(result.TrackedFileCleanupComplete, string.Join("; ", result.Warnings));
        Assert.Contains(result.Warnings, warning => warning.StartsWith(MissingFileWarning, StringComparison.Ordinal));
    }

    private async Task<string> CreateLibraryAsync(string name)
    {
        var root = FileService.GetTempDirectory(name);
        await _provider.GetRequiredService<IRootFolderService>().CreateAsync(new RootFolderBuilder()
            .WithPath(root).WithName("Tracked file gate root")
            .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive).Build());
        return root;
    }

    private async Task<(Audiobook Book, List<string> Parts)> AddBookAsync(
        string root, string title, string author, int partCount)
    {
        var folder = Directory.CreateDirectory(Path.Join(root, author, title)).FullName;
        var parts = new List<string>();
        for (var index = 1; index <= partCount; index++)
        {
            var path = Path.Join(folder, $"{title} Part {index}.m4b");
            File.WriteAllText(path, $"{title} audio {index}");
            parts.Add(path);
        }
        var book = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = title,
            BasePath = folder,
            FilePath = parts[0],
            Files = parts.Select(AudiobookFile.CreateUnresolved).ToList()
        });
        return (book, parts);
    }

    private Task<AudiobookFilesystemDeleteResult> DeleteAsync(Audiobook book) =>
        _provider.GetRequiredService<IAudiobookFilesystemDeleteService>().DeleteAsync(book, deleteFolder: true);

    private static Dictionary<string, string> Manifest(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "<dir>");

    private static bool IsWithin(string relativePath, string root, string folder) =>
        Path.Join(root, relativePath) is var full
        && (full == folder || full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static void AssertManifestLosesOnly(
        string root, Dictionary<string, string> before, params string[] deletedPaths)
    {
        var deleted = deletedPaths.Select(path => Path.GetRelativePath(root, path)).ToHashSet();
        var expected = before.Where(entry => !deleted.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal(expected, Manifest(root));
    }
}
