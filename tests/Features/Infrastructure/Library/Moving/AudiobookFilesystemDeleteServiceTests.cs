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

    [NetworkStorageTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeleteAsync_MountedDirectory_WaitsForReleasedFileCleanup(bool nested, bool owned)
    {
        var mount = Environment.GetEnvironmentVariable(NetworkStorageTheoryAttribute.PathEnvironmentVariable)!;
        var root = Directory.CreateDirectory(Path.Join(mount, "delete-release-" + Guid.NewGuid().ToString("N"))).FullName;
        FileStream? heldRead = null;
        Task? releaseTask = null;
        try
        {
            await _provider.GetRequiredService<IRootFolderService>().CreateAsync(new RootFolderBuilder()
                .WithPath(root).WithName("Delayed file cleanup root")
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive).Build());
            var folder = Directory.CreateDirectory(Path.Join(root, "Book")).FullName;
            var parent = nested ? Directory.CreateDirectory(Path.Join(folder, "Disc")).FullName : folder;
            var source = Path.Join(parent, "audio.m4b");
            await File.WriteAllTextAsync(source, "audio");
            if (owned)
            {
                var ownership = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
                foreach (var directory in new[] { parent, folder }.Distinct())
                    await ownership.RecordCreatedAsync(new LibraryDirectoryOwnershipClaim(
                        directory, FileSystemPathSemantics.CurrentHostDefault, "test"));
            }
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Delayed file cleanup",
                BasePath = folder,
                FilePath = source,
                Files = [AudiobookFile.CreateUnresolved(source)]
            });
            using var hook = PinnedFilesystemMutationHooks.PushBeforeUnixFileDeleteRevalidation(path =>
            {
                if (path != source || heldRead != null) return;
                heldRead = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                releaseTask = Task.Run(async () =>
                {
                    await Task.Delay(150);
                    heldRead.Dispose();
                });
            });

            var result = await _provider.GetRequiredService<IAudiobookFilesystemDeleteService>()
                .DeleteAsync(audiobook, deleteFolder: true);

            Assert.NotNull(releaseTask);
            Assert.True(result.DeletedFolder, string.Join("; ", result.Warnings));
            Assert.False(Directory.Exists(folder));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (releaseTask != null) await releaseTask;
            heldRead?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [LinuxFact]
    public async Task DeleteAsync_NewNestedEntryAfterPreflight_RetainsUnexpectedFile()
    {
        var root = FileService.GetTempDirectory("delete-unexpected-entry");
        await _provider.GetRequiredService<IRootFolderService>().CreateAsync(new RootFolderBuilder()
            .WithPath(root).WithName("Unexpected file root")
            .WithCaseSensitivityMode(OperatingSystem.IsWindows()
                ? FileSystemCaseSensitivityMode.Insensitive : FileSystemCaseSensitivityMode.Sensitive).Build());
        var folder = Directory.CreateDirectory(Path.Join(root, "Book")).FullName;
        var parent = Directory.CreateDirectory(Path.Join(folder, "Disc")).FullName;
        var source = Path.Join(parent, "audio.m4b");
        var unexpected = Path.Join(parent, "keep.txt");
        await File.WriteAllTextAsync(source, "audio");
        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Unexpected file",
            BasePath = folder,
            FilePath = source,
            Files = [AudiobookFile.CreateUnresolved(source)]
        });
        var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
        using var hook = PinnedFilesystemMutationHooks.PushBeforeUnixFileDeleteRevalidation(path =>
        {
            if (path == source) File.WriteAllText(unexpected, "retain");
        });
        var result = await service.DeleteAsync(audiobook, deleteFolder: true);

        Assert.False(result.DeletedFolder);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal("retain", await File.ReadAllTextAsync(unexpected));
        Assert.True(Directory.Exists(folder));
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
