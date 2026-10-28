using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class FileSystemSemanticsResolver : IFileSystemSemanticsResolver
{
    private const int MaxLinuxCaseProbeCandidates = 128;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileCaseSensitiveInfo = 23;
    private const uint FileCsFlagCaseSensitiveDir = 0x00000001;

    private const ulong FsIocGetFlags64 = 0x80086601;
    private const ulong FsIocGetFlags32 = 0x80046601;
    private const int FsCasefoldFlag = 0x40000000;
    private const long LinuxExtFamilySuperMagic = 0x0000ef53L;
    private const long LinuxF2fsSuperMagic = 0xf2f52010L;
    private const long LinuxTmpfsSuperMagic = 0x01021994L;
    private const long LinuxBcachefsSuperMagic = 0xca451a4eL;
    private const int LinuxStatFsBufferBytes = 256;
    // Darwin bsd/sys/unistd.h: _PC_CASE_SENSITIVE.
    private const int MacPathConfCaseSensitive = 11;

    private readonly Func<int, LinuxFilesystemFlagsProbe> _linuxFilesystemFlagsProbe;
    private readonly Func<string, FileAttributes> _pathAttributesProbe;

    public FileSystemSemanticsResolver()
        : this(ProbeLinuxFilesystemFlags)
    {
    }

    internal FileSystemSemanticsResolver(
        Func<int, LinuxFilesystemFlagsProbe> linuxFilesystemFlagsProbe,
        Func<string, FileAttributes>? pathAttributesProbe = null)
    {
        _linuxFilesystemFlagsProbe = linuxFilesystemFlagsProbe
            ?? throw new ArgumentNullException(nameof(linuxFilesystemFlagsProbe));
        _pathAttributesProbe = pathAttributesProbe ?? File.GetAttributes;
    }

    public ValueTask<FileSystemSemanticsResolution> ResolveAsync(
        string path,
        FileSystemCaseSensitivityMode mode,
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
            string explicitBoundary;
            try
            {
                explicitBoundary = FindExistingBoundary(fullPath)
                    ?? Path.GetPathRoot(fullPath)
                    ?? fullPath;
            }
            catch (Exception exception) when (IsBoundaryInspectionUnavailable(exception))
            {
                // Explicit semantics do not require probing the live filesystem.
                explicitBoundary = Path.GetPathRoot(fullPath) ?? fullPath;
            }
            return ValueTask.FromResult(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(syntax, explicitSensitivity),
                PathIdentityState.Valid,
                explicitBoundary,
                CanonicalPath: fullPath,
                EvidenceKind: FileSystemSemanticsEvidenceKind.Authoritative));
        }

        string? boundary;
        try
        {
            boundary = FindExistingBoundary(fullPath);
        }
        catch (Exception exception) when (IsBoundaryInspectionUnavailable(exception))
        {
            return ValueTask.FromResult(Unavailable(
                syntax,
                fullPath,
                $"Filesystem boundary could not be inspected safely: {exception.Message}"));
        }
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

    private FileSystemSemanticsResolution ResolveReadOnly(
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

        if (OperatingSystem.IsMacOS())
        {
            return ResolveMacOS(boundary, syntax);
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

    private static FileSystemSemanticsResolution ResolveMacOS(
        string boundary,
        FileSystemPathSyntax syntax)
    {
        Marshal.SetLastPInvokeError(0);
        var caseSensitive = PathConfUnix(
            boundary,
            MacPathConfCaseSensitive);
        if (caseSensitive == 0)
        {
            return Valid(
                syntax,
                boundary,
                FileSystemCaseSensitivity.Insensitive);
        }
        if (caseSensitive == 1)
        {
            return Valid(
                syntax,
                boundary,
                FileSystemCaseSensitivity.Sensitive);
        }

        var error = Marshal.GetLastPInvokeError();
        return Unavailable(
            syntax,
            boundary,
            error == 0
                ? "Filesystem case sensitivity could not be determined with pathconf."
                : $"Filesystem case sensitivity could not be read: {new Win32Exception(error).Message}");
    }

    private FileSystemSemanticsResolution ResolveLinux(
        string boundary,
        FileSystemPathSyntax syntax)
    {
        var descriptor = OpenUnix(
            boundary,
            UnixOpenFlags.Directory(noFollow: false));
        if (descriptor < 0)
        {
            return Unavailable(
                syntax,
                boundary,
                $"Filesystem case sensitivity could not be read: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        using var descriptorHandle = new SafeFileHandle(
            new IntPtr(descriptor),
            ownsHandle: true);
        var flagsProbe = _linuxFilesystemFlagsProbe(
            descriptorHandle.DangerousGetHandle().ToInt32());

        if (flagsProbe.Success
            && (flagsProbe.Flags & FsCasefoldFlag) != 0)
        {
            // FS_CASEFOLD_FL is positive proof of case-insensitive lookup. Its
            // absence is not portable negative proof: older kernels/filesystems
            // can successfully expose other inode flags without reporting
            // mount- or volume-level case-insensitive behavior. Fall through to
            // the read-only lookup probe instead of assuming sensitivity.
            return Valid(
                syntax,
                boundary,
                FileSystemCaseSensitivity.Insensitive);
        }
        if (flagsProbe.Success
            && IsDirectoryCasefoldFlagAuthoritativeFileSystem(
                flagsProbe.FileSystemType))
        {
            // These filesystems expose their case-insensitive lookup mode
            // through FS_CASEFOLD_FL. On them an unset bit is therefore
            // negative proof even when the directory is empty. Do not
            // generalize this to server-defined filesystems such as 9P, SMB,
            // NFS, or FUSE, where lookup semantics may be controlled remotely.
            return Valid(
                syntax,
                boundary,
                FileSystemCaseSensitivity.Sensitive);
        }

        var fallback = ProbeLinuxCaseSensitivityFromExistingEntry(
            boundary,
            syntax);
        if (fallback.State == PathIdentityState.Valid)
        {
            return fallback;
        }

        var nativeReason = flagsProbe.Success
            ? "the filesystem flags did not positively report case-insensitive lookup"
            : flagsProbe.ErrorCode == 0
                ? "the filesystem flags ioctl was unavailable"
                : new Win32Exception(flagsProbe.ErrorCode).Message;
        return Unavailable(
            syntax,
            boundary,
            $"The filesystem flags probe could not determine case sensitivity ({nativeReason}), and the read-only existing-entry probe was inconclusive: {fallback.Reason ?? "no suitable stable entry was available"}. Select Sensitive or Insensitive explicitly.");
    }

    private static FileSystemSemanticsResolution ProbeLinuxCaseSensitivityFromExistingEntry(
        string boundary,
        FileSystemPathSyntax syntax)
    {
        try
        {
            using var pinned = PinnedDirectoryCreation.OpenPinnedBoundary(boundary);
            var attempted = 0;
            string? lastReason = null;
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(boundary))
            {
                if (attempted >= MaxLinuxCaseProbeCandidates)
                {
                    break;
                }

                var name = Path.GetFileName(entryPath);
                if (!TryCreateAsciiCaseVariant(name, out var alternateName))
                {
                    continue;
                }

                attempted++;
                var outcome = pinned.ProbeLinuxCaseAlias(
                    name,
                    alternateName,
                    out var reason);
                switch (outcome)
                {
                    case PinnedDirectoryCreation.LinuxCaseAliasProbeOutcome.Sensitive:
                        return Valid(
                            syntax,
                            boundary,
                            FileSystemCaseSensitivity.Sensitive,
                            FileSystemSemanticsEvidenceKind.BehavioralObservation);
                    case PinnedDirectoryCreation.LinuxCaseAliasProbeOutcome.Insensitive:
                        return Valid(
                            syntax,
                            boundary,
                            FileSystemCaseSensitivity.Insensitive,
                            FileSystemSemanticsEvidenceKind.BehavioralObservation);
                    case PinnedDirectoryCreation.LinuxCaseAliasProbeOutcome.Unavailable:
                        return Unavailable(
                            syntax,
                            boundary,
                            reason ?? "The filesystem boundary changed during case-sensitivity probing.");
                    default:
                        lastReason = reason ?? lastReason;
                        break;
                }
            }

            return Unavailable(
                syntax,
                boundary,
                lastReason
                    ?? "No stable existing entry with an ASCII case variant was available for a read-only case-sensitivity probe.");
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or InvalidOperationException or NotSupportedException)
        {
            return Unavailable(
                syntax,
                boundary,
                exception.Message);
        }
    }

    private static bool TryCreateAsciiCaseVariant(
        string name,
        out string alternateName)
    {
        var characters = name.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (character is >= 'a' and <= 'z')
            {
                characters[index] = char.ToUpperInvariant(character);
                alternateName = new string(characters);
                return true;
            }
            if (character is >= 'A' and <= 'Z')
            {
                characters[index] = char.ToLowerInvariant(character);
                alternateName = new string(characters);
                return true;
            }
        }

        alternateName = name;
        return false;
    }

    private static LinuxFilesystemFlagsProbe ProbeLinuxFilesystemFlags(
        int descriptor)
    {
        int result;
        int flags;
        if (IntPtr.Size == sizeof(long))
        {
            result = IoctlUnix64(
                descriptor,
                FsIocGetFlags64,
                out var nativeFlags);
            flags = unchecked((int)nativeFlags);
        }
        else
        {
            result = IoctlUnix32(
                descriptor,
                FsIocGetFlags32,
                out flags);
        }

        if (result == 0)
        {
            return new LinuxFilesystemFlagsProbe(
                true,
                flags,
                0,
                TryGetLinuxFileSystemType(descriptor));
        }

        return new LinuxFilesystemFlagsProbe(
            false,
            0,
            Marshal.GetLastWin32Error());
    }

    internal readonly record struct LinuxFilesystemFlagsProbe(
        bool Success,
        int Flags,
        int ErrorCode,
        long? FileSystemType = null);

    private static bool IsDirectoryCasefoldFlagAuthoritativeFileSystem(
        long? fileSystemType) =>
        fileSystemType is LinuxExtFamilySuperMagic
            or LinuxF2fsSuperMagic
            or LinuxTmpfsSuperMagic
            or LinuxBcachefsSuperMagic;

    private string? FindExistingBoundary(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = _pathAttributesProbe(current);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    return current;
                }
            }
            catch (Exception exception) when (
                FileSystemSafety.IsProvenMissingPathException(exception))
            {
                // A proven missing segment may safely fall back to its existing parent.
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static bool IsBoundaryInspectionUnavailable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception;

    private static FileSystemSemanticsResolution Valid(
        FileSystemPathSyntax syntax,
        string boundary,
        FileSystemCaseSensitivity sensitivity,
        FileSystemSemanticsEvidenceKind evidenceKind =
            FileSystemSemanticsEvidenceKind.Authoritative) =>
        new(
            new FileSystemPathSemantics(syntax, sensitivity),
            PathIdentityState.Valid,
            boundary,
            EvidenceKind: evidenceKind);

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
            boundary,
            FileSystemSemanticsEvidenceKind.Unavailable);

}
