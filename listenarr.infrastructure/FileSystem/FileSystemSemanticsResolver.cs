using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public sealed class FileSystemSemanticsResolver : IFileSystemSemanticsResolver
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileCaseSensitiveInfo = 23;
    private const uint FileCsFlagCaseSensitiveDir = 0x00000001;

    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenCloseOnExec = 0x80000;
    private const ulong FsIocGetFlags = 0x80086601;
    private const int FsCasefoldFlag = 0x40000000;

    public ValueTask<FileSystemSemanticsResolution> ResolveAsync(
        string path,
        FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "Filesystem semantics require an absolute path.",
                nameof(path));
        }

        var syntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix;
        var fullPath = FileUtils.NormalizeStoredPath(path);

        if (mode != FileSystemCaseSensitivityMode.Auto)
        {
            var explicitSensitivity = mode == FileSystemCaseSensitivityMode.Sensitive
                ? FileSystemCaseSensitivity.Sensitive
                : FileSystemCaseSensitivity.Insensitive;
            return ValueTask.FromResult(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(syntax, explicitSensitivity),
                PathIdentityState.Valid,
                FindExistingBoundary(fullPath) ?? Path.GetPathRoot(fullPath) ?? fullPath,
                CanonicalPath: fullPath));
        }

        var boundary = FindExistingBoundary(fullPath);
        if (boundary == null)
        {
            return ValueTask.FromResult(Unavailable(
                syntax,
                fullPath,
                "No existing filesystem boundary could be found."));
        }

        var resolution = ResolveReadOnly(boundary, syntax);
        return ValueTask.FromResult(resolution with { CanonicalPath = fullPath });
    }

    private static FileSystemSemanticsResolution ResolveReadOnly(
        string boundary,
        FileSystemPathSyntax syntax)
    {
        if (OperatingSystem.IsWindows())
        {
            return ResolveWindows(boundary, syntax);
        }

        if (OperatingSystem.IsLinux())
        {
            return ResolveLinux(boundary, syntax);
        }

        return Unavailable(
            syntax,
            boundary,
            "Automatic case-sensitivity detection is unavailable on this host without writing a probe. Select Sensitive or Insensitive explicitly.");
    }

    private static FileSystemSemanticsResolution ResolveWindows(
        string boundary,
        FileSystemPathSyntax syntax)
    {
        using var handle = CreateFileWindows(
            boundary,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Unavailable(
                syntax,
                boundary,
                $"Filesystem case sensitivity could not be read: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileCaseSensitiveInfo,
                out var info,
                (uint)Marshal.SizeOf<FileCaseSensitiveInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            // Older Windows/filesystems do not expose per-directory case-sensitive
            // mode. Their Win32 namespace is case-insensitive.
            if (error is 1 or 50 or 87)
            {
                return Valid(
                    syntax,
                    boundary,
                    FileSystemCaseSensitivity.Insensitive);
            }

            return Unavailable(
                syntax,
                boundary,
                $"Filesystem case sensitivity could not be read: {new Win32Exception(error).Message}");
        }

        return Valid(
            syntax,
            boundary,
            (info.Flags & FileCsFlagCaseSensitiveDir) != 0
                ? FileSystemCaseSensitivity.Sensitive
                : FileSystemCaseSensitivity.Insensitive);
    }

    private static FileSystemSemanticsResolution ResolveLinux(
        string boundary,
        FileSystemPathSyntax syntax)
    {
        var descriptor = OpenUnix(
            boundary,
            OpenReadOnly | OpenDirectory | OpenCloseOnExec);
        if (descriptor < 0)
        {
            return Unavailable(
                syntax,
                boundary,
                $"Filesystem case sensitivity could not be read: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        try
        {
            if (IoctlUnix(descriptor, FsIocGetFlags, out var flags) == 0)
            {
                return Valid(
                    syntax,
                    boundary,
                    (flags & FsCasefoldFlag) != 0
                        ? FileSystemCaseSensitivity.Insensitive
                        : FileSystemCaseSensitivity.Sensitive);
            }

            return Unavailable(
                syntax,
                boundary,
                "The filesystem does not expose read-only case-sensitivity metadata. Select Sensitive or Insensitive explicitly.");
        }
        finally
        {
            _ = CloseUnix(descriptor);
        }
    }

    private static string? FindExistingBoundary(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static FileSystemSemanticsResolution Valid(
        FileSystemPathSyntax syntax,
        string boundary,
        FileSystemCaseSensitivity sensitivity) =>
        new(
            new FileSystemPathSemantics(syntax, sensitivity),
            PathIdentityState.Valid,
            boundary);

    private static FileSystemSemanticsResolution Unavailable(
        FileSystemPathSyntax syntax,
        string boundary,
        string reason) =>
        new(
            new FileSystemPathSemantics(
                syntax,
                FileSystemCaseSensitivity.Unknown),
            PathIdentityState.Unavailable,
            boundary,
            reason,
            boundary);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWindows(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileCaseSensitiveInformation fileInformation,
        uint bufferSize);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlUnix(
        int descriptor,
        ulong request,
        out int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseUnix(int descriptor);
}
