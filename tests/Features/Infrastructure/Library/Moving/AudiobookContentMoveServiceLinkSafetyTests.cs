using System.Diagnostics;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [DirectoryLinkFact]
    public async Task MoveContentsAsync_SourceDirectoryLink_BlocksAtomicRename()
    {
        var externalSource = FileService.GetTempDirectory("content-move-root-link-external");
        var externalFile = await FileService.GetFileAsync(externalSource, "book.m4b", "external audio");
        var linkParent = FileService.GetTempDirectory("content-move-root-link-parent");
        var sourceLink = Path.Join(linkParent, "linked-source");
        Assert.True(
            TryCreateDirectoryLink(sourceLink, externalSource),
            "The required directory link could not be created.");

        try
        {
            var target = Path.Join(linkParent, $"target-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(sourceLink, target);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(Directory.Exists(sourceLink));
            Assert.True(File.Exists(externalFile));
            Assert.Equal("external audio", await File.ReadAllTextAsync(externalFile));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            TryRemoveDirectoryLink(sourceLink);
        }
    }

    [WindowsFact]
    public async Task MoveContentsAsync_WindowsSourceJunction_BlocksAtomicRename()
    {

        var externalSource = FileService.GetTempDirectory("content-move-root-junction-external");
        var externalFile = await FileService.GetFileAsync(externalSource, "book.m4b", "external audio");
        var linkParent = FileService.GetTempDirectory("content-move-root-junction-parent");
        var sourceJunction = Path.Join(linkParent, "junction-source");
        Assert.True(
            TryCreateWindowsJunction(sourceJunction, externalSource),
            "The required Windows junction could not be created.");

        try
        {
            var target = Path.Join(linkParent, $"target-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(sourceJunction, target);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(Directory.Exists(sourceJunction));
            Assert.True(File.Exists(externalFile));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            TryRemoveDirectoryLink(sourceJunction);
        }
    }

    [DirectoryLinkFact]
    public async Task MoveContentsAsync_NestedDirectoryLink_BlocksAtomicRename()
    {
        var source = FileService.GetTempDirectory("content-move-nested-link-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var external = FileService.GetTempDirectory("content-move-nested-link-external");
        var externalFile = await FileService.GetFileAsync(external, "external.txt", "external");
        var nestedLink = Path.Join(source, "linked");
        Assert.True(
            TryCreateDirectoryLink(nestedLink, external),
            "The required directory link could not be created.");

        try
        {
            var target = Path.Join(FileService.GetTempPath(), $"content-move-nested-link-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(externalFile));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            TryRemoveDirectoryLink(nestedLink);
        }
    }

    [DirectoryLinkFact]
    public async Task MoveContentsAsync_TargetParentReplacedByLinkAfterValidation_DoesNotPublishOutsideTarget()
    {
        var root = FileService.GetTempDirectory("content-move-target-parent-race-root");
        var source = Path.Join(root, "source");
        var sourceDisc = Path.Join(source, "CD1");
        Directory.CreateDirectory(sourceDisc);
        var sourceFile = await FileService.GetFileAsync(
            sourceDisc,
            "book.m4b",
            "audio");
        var target = Path.Join(root, "target");
        var targetDisc = Path.Join(target, "CD1");
        var displacedTargetDisc = Path.Join(root, "target-cd1-original");
        var external = FileService.GetTempDirectory(
            "content-move-target-parent-race-external");
        var request = await CreateLeasedMoveRequestAsync(
            source,
            target,
            sourceCleanupBoundary: root,
            executionProtocolVersion:
                MoveExecutionProtocol.MarkerlessDatabaseState);
        var hookRan = false;
        void ReplaceTargetParent(string path)
        {
            if (hookRan
                || !string.Equals(path, targetDisc, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            Directory.Move(targetDisc, displacedTargetDisc);
            Assert.True(
                TryCreateDirectoryLink(targetDisc, external),
                "The target-parent replacement link could not be created.");
        }

        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(
            ReplaceTargetParent);
        try
        {
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(hookRan);
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.False(File.Exists(Path.Join(external, "book.m4b")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.True(Directory.Exists(displacedTargetDisc));
            Assert.Empty(Directory.EnumerateFileSystemEntries(displacedTargetDisc));
        }
        finally
        {
            TryRemoveDirectoryLink(targetDisc);
            if (Directory.Exists(displacedTargetDisc)
                && !Directory.Exists(targetDisc))
            {
                Directory.Move(displacedTargetDisc, targetDisc);
            }
        }
    }

    [WindowsFact]
    public async Task MoveContentsAsync_TargetParentReplacedByJunctionAfterValidation_DoesNotPublishOutsideTarget()
    {
        var root = FileService.GetTempDirectory(
            "content-move-target-parent-junction-race-root");
        var source = Path.Join(root, "source");
        var sourceDisc = Path.Join(source, "CD1");
        Directory.CreateDirectory(sourceDisc);
        var sourceFile = await FileService.GetFileAsync(
            sourceDisc,
            "book.m4b",
            "audio");
        var target = Path.Join(root, "target");
        var targetDisc = Path.Join(target, "CD1");
        var displacedTargetDisc = Path.Join(root, "target-cd1-original");
        var external = FileService.GetTempDirectory(
            "content-move-target-parent-junction-race-external");
        var request = await CreateLeasedMoveRequestAsync(
            source,
            target,
            sourceCleanupBoundary: root,
            executionProtocolVersion:
                MoveExecutionProtocol.MarkerlessDatabaseState);
        var hookRan = false;
        void ReplaceTargetParent(string path)
        {
            if (hookRan
                || !string.Equals(path, targetDisc, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            Directory.Move(targetDisc, displacedTargetDisc);
            Assert.True(
                TryCreateWindowsJunction(targetDisc, external),
                "The target-parent replacement junction could not be created.");
        }

        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(
            ReplaceTargetParent);
        try
        {
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(hookRan);
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.False(File.Exists(Path.Join(external, "book.m4b")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.True(Directory.Exists(displacedTargetDisc));
            Assert.Empty(Directory.EnumerateFileSystemEntries(displacedTargetDisc));
        }
        finally
        {
            TryRemoveDirectoryLink(targetDisc);
            if (Directory.Exists(displacedTargetDisc)
                && !Directory.Exists(targetDisc))
            {
                Directory.Move(displacedTargetDisc, targetDisc);
            }
        }
    }

    [FileLinkFact]
    public async Task MoveContentsAsync_NestedFileSymlink_BlocksAtomicRename()
    {
        var source = FileService.GetTempDirectory("content-move-file-link-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var external = FileService.GetTempDirectory("content-move-file-link-external");
        var externalFile = await FileService.GetFileAsync(external, "external.txt", "external");
        var linkedFile = Path.Join(source, "linked.txt");
        try
        {
            File.CreateSymbolicLink(linkedFile, externalFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException(
                $"This native filesystem regression requires symbolic-link support: {exception.Message}");
        }

        try
        {
            var target = Path.Join(FileService.GetTempPath(), $"content-move-file-link-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(linkedFile));
            Assert.True(File.Exists(externalFile));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            if (File.Exists(linkedFile))
            {
                File.Delete(linkedFile);
            }
        }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return OperatingSystem.IsWindows()
                && TryCreateWindowsJunction(linkPath, targetPath);
        }
    }

    private static bool TryCreateWindowsJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryRemoveDirectoryLink(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to remove test directory link '{linkPath}': {exception.Message}");
        }
    }
}
