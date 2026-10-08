using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "AudiobookFilesystemDeleteServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookFilesystemDeleteServiceTests : BaseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task DeleteAsync_LargeTree_BoundsPreflightAndMutationHandles(bool siblingDirectories, bool owned) =>
        DeleteLargeTreeAsync(FileService.GetTempDirectory("delete-bounded-handles"), 600, siblingDirectories, owned);

    [NetworkStorageTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_LargeMountedTree_BoundsHandles(bool siblingDirectories)
    {
        var mount = Environment.GetEnvironmentVariable(NetworkStorageTheoryAttribute.PathEnvironmentVariable)!;
        var root = Directory.CreateDirectory(Path.Join(mount, "delete-bounded-" + Guid.NewGuid().ToString("N"))).FullName;
        try { await DeleteLargeTreeAsync(root, 240, siblingDirectories, false); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private async Task DeleteLargeTreeAsync(string root, int count, bool siblingDirectories, bool owned)
    {
        await _provider.GetRequiredService<IRootFolderService>().CreateAsync(new RootFolderBuilder()
            .WithPath(root).WithName("Bounded deletion root")
            .WithCaseSensitivityMode(OperatingSystem.IsWindows()
                ? FileSystemCaseSensitivityMode.Insensitive : FileSystemCaseSensitivityMode.Sensitive).Build());
        var folder = Directory.CreateDirectory(Path.Join(root, "Book")).FullName;
        var files = new List<AudiobookFile>();
        var store = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        for (var index = 0; index < count; index++)
        {
            var parent = siblingDirectories
                ? Directory.CreateDirectory(Path.Join(folder, $"Disc {index:D4}")).FullName : folder;
            var path = Path.Join(parent, $"{index:D4}.m4b");
            File.WriteAllText(path, "audio");
            files.Add(AudiobookFile.CreateUnresolved(path));
            if (owned && siblingDirectories)
                await store.RecordCreatedAsync(new LibraryDirectoryOwnershipClaim(
                    parent, FileSystemPathSemantics.CurrentHostDefault, "test"));
        }
        File.WriteAllText(Path.Join(folder, "cover.txt"), "companion");
        if (owned)
            await store.RecordCreatedAsync(new LibraryDirectoryOwnershipClaim(
                folder, FileSystemPathSemantics.CurrentHostDefault, "test"));
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Large deletion",
            BasePath = folder,
            FilePath = files[0].Path,
            Files = files
        });
        var logger = new Mock<ILogger<AudiobookFilesystemDeleteService>>();
        var service = ActivatorUtilities.CreateInstance<AudiobookFilesystemDeleteService>(_provider, logger.Object);
        var observed = 0;
        void ObserveHandles()
        {
            if (!OperatingSystem.IsLinux()) return;
            observed = Math.Max(observed, Directory.EnumerateFiles("/proc/self/fd")
                .Count(path => new FileInfo(path).LinkTarget is { } target
                    && (target == root || target.StartsWith(root + "/", StringComparison.Ordinal))));
            Assert.InRange(observed, 0, 48);
        }
        service.AfterTrackedContentCaptureForTest = () =>
        {
            ObserveHandles();
            if (!OperatingSystem.IsWindows()) return;
            foreach (var file in files)
            {
                using var exclusive = File.Open(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.Equal(5, exclusive.Length);
            }
        };
        using var fileHook = PinnedFilesystemMutationHooks.PushBeforeUnixFileDeleteRevalidation(_ => ObserveHandles());
        using var directoryHook = PinnedFilesystemMutationHooks.PushBeforeUnixDirectoryDeleteRevalidation(_ => ObserveHandles());

        var result = await service.DeleteAsync(audiobook, deleteFolder: true);

        var diagnostics = string.Join("; ", result.Warnings.Concat(logger.Invocations
            .Select(invocation => string.Join(" ", invocation.Arguments.Select(argument => argument?.ToString())))));
        Assert.True(result.TrackedFileCleanupComplete, diagnostics);
        Assert.True(result.DeletedFiles == count + 1, $"Deleted {result.DeletedFiles}/{count + 1}: {diagnostics}");
        Assert.True(result.DeletedFolder, string.Join("; ", result.Warnings));
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(root));
    }
}
