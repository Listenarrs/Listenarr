using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "LocalFileSystemTests")]
[Trait("Category", "Infrastructure")]
public sealed class LocalFileSystemTests : BaseTests
{
    [DirectoryLinkFact]
    public void DeleteEmptyDirectories_AncestorLinkSwap_PreservesExternalDirectory()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "empty-cleanup-root-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Join(root, "parent");
        var capturedParent = Path.Join(root, "captured-parent");
        var candidate = Path.Join(parent, "empty");
        var externalParent = Path.Join(
            Path.GetTempPath(),
            "empty-cleanup-external-" + Guid.NewGuid().ToString("N"));
        var externalCandidate = Path.Join(externalParent, "empty");
        Directory.CreateDirectory(candidate);
        Directory.CreateDirectory(externalCandidate);
        var swapped = false;
        try
        {
            using var hook = FileSystemSafety.PushBeforeEmptyDirectoryCandidatePinHook(
                path =>
                {
                    if (swapped
                        || !string.Equals(
                            Path.GetFullPath(path),
                            Path.GetFullPath(candidate),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    Directory.Move(parent, capturedParent);
                    CreateDirectoryLink(parent, externalParent);
                    swapped = true;
                });
            var fileSystem = new LocalFileSystem();

            fileSystem.DeleteEmptyDirectories(root);

            Assert.True(swapped);
            Assert.True(Directory.Exists(externalCandidate));
            Assert.True(Directory.Exists(capturedParent));
        }
        finally
        {
            TryDeleteDirectoryLink(parent);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
            if (Directory.Exists(externalParent))
            {
                Directory.Delete(externalParent, true);
            }
        }
    }

    [DirectoryLinkFact]
    public void DeleteEmptyDirectories_RootLinkSwap_PreservesCapturedAndExternalDirectories()
    {
        var container = Path.Join(
            Path.GetTempPath(),
            "empty-cleanup-container-" + Guid.NewGuid().ToString("N"));
        var root = Path.Join(container, "root");
        var capturedRoot = Path.Join(container, "captured-root");
        var externalRoot = Path.Join(
            Path.GetTempPath(),
            "empty-cleanup-root-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(externalRoot);
        var swapped = false;
        try
        {
            using var hook = FileSystemSafety.PushBeforeEmptyDirectoryCandidatePinHook(
                path =>
                {
                    if (swapped
                        || !string.Equals(
                            Path.GetFullPath(path),
                            Path.GetFullPath(root),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    Directory.Move(root, capturedRoot);
                    CreateDirectoryLink(root, externalRoot);
                    swapped = true;
                });

            new LocalFileSystem().DeleteEmptyDirectories(root);

            Assert.True(swapped);
            Assert.True(Directory.Exists(externalRoot));
            Assert.True(Directory.Exists(capturedRoot));
        }
        finally
        {
            TryDeleteDirectoryLink(root);
            if (Directory.Exists(container))
            {
                Directory.Delete(container, true);
            }
            if (Directory.Exists(externalRoot))
            {
                Directory.Delete(externalRoot, true);
            }
        }
    }

    [DirectoryLinkFact]
    public void DeleteEmptyDirectories_ReplacementAfterPin_PreservesBothGenerations()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "empty-cleanup-pinned-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Join(root, "parent");
        var candidate = Path.Join(parent, "empty");
        var capturedCandidate = Path.Join(parent, "captured-empty");
        var externalCandidate = Path.Join(
            Path.GetTempPath(),
            "empty-cleanup-pinned-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(candidate);
        Directory.CreateDirectory(externalCandidate);
        var swapped = false;
        try
        {
            using var hook = FileSystemSafety.PushAfterEmptyDirectoryCandidatePinHook(
                path =>
                {
                    if (swapped
                        || !string.Equals(
                            Path.GetFullPath(path),
                            Path.GetFullPath(candidate),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    Directory.Move(candidate, capturedCandidate);
                    CreateDirectoryLink(candidate, externalCandidate);
                    swapped = true;
                });

            new LocalFileSystem().DeleteEmptyDirectories(root);

            Assert.True(swapped);
            Assert.True(Directory.Exists(externalCandidate));
            Assert.True(Directory.Exists(capturedCandidate));
        }
        finally
        {
            TryDeleteDirectoryLink(candidate);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
            if (Directory.Exists(externalCandidate))
            {
                Directory.Delete(externalCandidate, true);
            }
        }
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start junction creation.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException(process.StandardError.ReadToEnd());
        }
    }

    private static void TryDeleteDirectoryLink(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath)
                && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(linkPath);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
    }

    [LinuxDirectoryAndFileLinkFact]
    public void IsReparsePoint_DetectsLinuxSymbolicLinksOnly()
    {

        var root = Path.Join(
            Path.GetTempPath(),
            "local-filesystem-link-" + Guid.NewGuid().ToString("N"));
        var targetDirectory = Path.Join(root, "target-directory");
        var targetFile = Path.Join(root, "target-file.m4b");
        var directoryLink = Path.Join(root, "directory-link");
        var fileLink = Path.Join(root, "file-link.m4b");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(targetFile, "audio");
        try
        {
            Directory.CreateSymbolicLink(directoryLink, targetDirectory);
            File.CreateSymbolicLink(fileLink, targetFile);
            var fileSystem = new LocalFileSystem();

            Assert.True(fileSystem.IsReparsePoint(directoryLink));
            Assert.True(fileSystem.IsReparsePoint(fileLink));
            Assert.False(fileSystem.IsReparsePoint(targetDirectory));
            Assert.False(fileSystem.IsReparsePoint(targetFile));
        }
        finally
        {
            if (Directory.Exists(directoryLink))
            {
                Directory.Delete(directoryLink);
            }

            if (File.Exists(fileLink))
            {
                File.Delete(fileLink);
            }

            Directory.Delete(root, true);
        }
    }
}
