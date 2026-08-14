using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileSystemSafetyDeletionTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileSystemSafetyDeletionTests : BaseTests
{
    [Fact]
    public void TryDeleteFile_MissingLeafWithStableParent_IsProvenAbsent()
    {
        var root = FileService.GetTempDirectory("filesystem-safety-missing-file");
        var missing = Path.Join(root, "missing.m4b");

        var result = FileSystemSafety.TryDeleteFile(
            missing,
            [root],
            out var reason);

        Assert.True(result, reason);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task TryDeleteFile_ExistingFile_DeletesPinnedEntry()
    {
        var root = FileService.GetTempDirectory("filesystem-safety-delete-file");
        var file = await FileService.GetFileAsync(root, "book.m4b", "audio");

        var result = FileSystemSafety.TryDeleteFile(
            file,
            [root],
            out var reason);

        Assert.True(result, reason);
        Assert.False(File.Exists(file));
    }

    [DirectoryLinkFact]
    public async Task TryDeleteFile_IntermediateAncestorReplacedByLink_DoesNotDeleteExternalFile()
    {
        var root = FileService.GetTempDirectory(
            "filesystem-safety-delete-ancestor-race-root");
        var author = Path.Join(root, "Author");
        var book = Path.Join(author, "Book");
        Directory.CreateDirectory(book);
        var originalFile = await FileService.GetFileAsync(
            book,
            "book.m4b",
            "original");
        var displacedAuthor = Path.Join(root, "Author.original");
        var external = FileService.GetTempDirectory(
            "filesystem-safety-delete-ancestor-race-external");
        var externalBook = Path.Join(external, "Book");
        Directory.CreateDirectory(externalBook);
        var externalFile = await FileService.GetFileAsync(
            externalBook,
            "book.m4b",
            "external");
        var parentPath = Path.GetDirectoryName(originalFile)!;
        var hookRan = false;

        void ReplaceIntermediateAncestor(string path)
        {
            if (hookRan
                || !string.Equals(path, parentPath, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            Directory.Move(author, displacedAuthor);
            Assert.True(
                TryCreateDirectoryLink(author, external),
                "The intermediate-ancestor replacement link could not be created.");
        }

        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(
            ReplaceIntermediateAncestor);
        try
        {
            var result = FileSystemSafety.TryDeleteFile(
                originalFile,
                [root],
                out _);

            Assert.True(hookRan);
            Assert.False(result);
            Assert.Equal("external", await File.ReadAllTextAsync(externalFile));
            Assert.Equal(
                "original",
                await File.ReadAllTextAsync(
                    Path.Join(displacedAuthor, "Book", "book.m4b")));
        }
        finally
        {
            TryRemoveDirectoryLink(author);
            if (Directory.Exists(displacedAuthor) && !Directory.Exists(author))
            {
                Directory.Move(displacedAuthor, author);
            }
        }
    }

    [WindowsFact]
    public async Task TryDeleteFile_IntermediateAncestorReplacedByJunction_DoesNotDeleteExternalFile()
    {
        var root = FileService.GetTempDirectory(
            "filesystem-safety-delete-ancestor-junction-race-root");
        var author = Path.Join(root, "Author");
        var book = Path.Join(author, "Book");
        Directory.CreateDirectory(book);
        var originalFile = await FileService.GetFileAsync(
            book,
            "book.m4b",
            "original");
        var displacedAuthor = Path.Join(root, "Author.original");
        var external = FileService.GetTempDirectory(
            "filesystem-safety-delete-ancestor-junction-race-external");
        var externalBook = Path.Join(external, "Book");
        Directory.CreateDirectory(externalBook);
        var externalFile = await FileService.GetFileAsync(
            externalBook,
            "book.m4b",
            "external");
        var parentPath = Path.GetDirectoryName(originalFile)!;
        var hookRan = false;

        void ReplaceIntermediateAncestor(string path)
        {
            if (hookRan
                || !string.Equals(path, parentPath, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            Directory.Move(author, displacedAuthor);
            Assert.True(
                TryCreateWindowsJunctionOnly(author, external),
                "The intermediate-ancestor replacement junction could not be created.");
        }

        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(
            ReplaceIntermediateAncestor);
        try
        {
            var result = FileSystemSafety.TryDeleteFile(
                originalFile,
                [root],
                out _);

            Assert.True(hookRan);
            Assert.False(result);
            Assert.Equal("external", await File.ReadAllTextAsync(externalFile));
            Assert.Equal(
                "original",
                await File.ReadAllTextAsync(
                    Path.Join(displacedAuthor, "Book", "book.m4b")));
        }
        finally
        {
            TryRemoveDirectoryLink(author);
            if (Directory.Exists(displacedAuthor) && !Directory.Exists(author))
            {
                Directory.Move(displacedAuthor, author);
            }
        }
    }

    [Fact]
    public async Task TryDeleteEmptyDirectory_FileAtTarget_IsNotReportedAbsent()
    {
        var root = FileService.GetTempDirectory("filesystem-safety-directory-file");
        var file = await FileService.GetFileAsync(root, "occupied", "content");

        var result = FileSystemSafety.TryDeleteEmptyDirectory(
            file,
            [root],
            out _);

        Assert.False(result);
        Assert.True(File.Exists(file));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task TryDeleteFile_InaccessibleParent_IsNotTreatedAsMissing()
    {
        Assert.NotEqual(
            (uint)0,
            GetEffectiveUserId());

        var root = FileService.GetTempDirectory(
            "filesystem-safety-inaccessible-file");
        var parent = Path.Join(root, "protected");
        Directory.CreateDirectory(parent);
        var file = await FileService.GetFileAsync(parent, "book.m4b", "audio");
        var originalMode = File.GetUnixFileMode(parent);
        File.SetUnixFileMode(parent, UnixFileMode.None);
        try
        {
            Assert.False(File.Exists(file));

            var result = FileSystemSafety.TryDeleteFile(
                file,
                [root],
                out var reason);

            Assert.False(result);
            Assert.Contains(
                "failed safely",
                reason,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(parent, originalMode);
        }

        Assert.True(File.Exists(file));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void UnixAccessDenied_IsNotClassifiedAsProvenMissing()
    {
        Assert.False(FileSystemSafety.IsProvenMissingPathException(
            new System.ComponentModel.Win32Exception(13, "Permission denied")));
        Assert.True(FileSystemSafety.IsProvenMissingPathException(
            new System.ComponentModel.Win32Exception(2, "No such file or directory")));
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

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
            catch (Exception junctionException) when (junctionException is
                IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static bool TryCreateWindowsJunctionOnly(string linkPath, string targetPath)
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
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryRemoveDirectoryLink(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(
                $"Failed to remove test directory link '{path}': {exception.Message}");
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
