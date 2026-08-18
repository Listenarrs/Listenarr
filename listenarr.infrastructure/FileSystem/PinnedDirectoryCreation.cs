using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation : IDisposable
{
    private const int UnixAlreadyExists = 17;
    private const int UnixNoEntry = 2;
    private const uint UnixDirectoryMode = 0x1FF;
    private const uint UnixFileMode = 0x180;
    private const int AtRemovedirLinux = 0x200;
    private const int AtRemovedirMac = 0x80;
    private const uint RenameNoReplace = 1;
    private const uint RenameExchange = 2;
    private const uint RenameSwapMac = 2;
    private const uint RenameExclusiveMac = 4;

    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileWriteAttributes = 0x0100;
    private const uint Synchronize = 0x00100000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint FileShareDelete = 0x00000004;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeHidden = 0x00000002;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);

    private readonly SafeFileHandle _parentHandle;
    private readonly SafeFileHandle? _directoryHandle;
    private readonly string _parentPath;
    private readonly string _childName;
    private readonly bool _parentFollowsVisibleFinalLink;
    private bool _disposed;

    private PinnedDirectoryCreation(
        SafeFileHandle parentHandle,
        SafeFileHandle? directoryHandle,
        string parentPath,
        string childName,
        bool created,
        bool parentFollowsVisibleFinalLink)
    {
        _parentHandle = parentHandle;
        _directoryHandle = directoryHandle;
        _parentPath = parentPath;
        _childName = childName;
        _parentFollowsVisibleFinalLink =
            parentFollowsVisibleFinalLink;
        Created = created;
    }

    public bool Created { get; }

    internal bool CreationGenerationIsProvable =>
        Created && OperatingSystem.IsWindows();

    public string FullPath => Path.Join(_parentPath, _childName);

    public static PinnedDirectoryCreation TryCreate(string parentPath, string childName) =>
        TryCreateCore(parentPath, childName, requireDirectoryDeleteAccess: false);

    internal static PinnedDirectoryCreation TryCreateForPublication(
        string parentPath,
        string childName) =>
        TryCreateCore(parentPath, childName, requireDirectoryDeleteAccess: true);

    private static PinnedDirectoryCreation TryCreateCore(
        string parentPath,
        string childName,
        bool requireDirectoryDeleteAccess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ValidateLeafName(childName);
        ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(parentPath);

        return OperatingSystem.IsWindows()
            ? TryCreateWindows(parentPath, childName, requireDirectoryDeleteAccess)
            : TryCreateUnix(parentPath, childName);
    }

    public bool VisiblePathMatches() =>
        ProbeVisiblePathMatch() == RegistrationPublicationMatchOutcome.Match;

    internal RegistrationPublicationMatchOutcome ProbeVisiblePathMatch()
    {
        ThrowIfDisposed();
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }

        try
        {
            using var visible = OpenVisibleDirectory(FullPath);
            return HandlesIdentifySameDirectory(_directoryHandle, visible)
                ? RegistrationPublicationMatchOutcome.Match
                : RegistrationPublicationMatchOutcome.Mismatch;
        }
        catch (FileNotFoundException)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
        catch (DirectoryNotFoundException)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
        catch (Win32Exception exception) when (
            OperatingSystem.IsWindows()
                ? exception.NativeErrorCode is 2 or 3
                : exception.NativeErrorCode == 2)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or PlatformNotSupportedException)
        {
            return RegistrationPublicationMatchOutcome.Unavailable;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _directoryHandle?.Dispose();
        _parentHandle.Dispose();
        _disposed = true;
    }

    private static PinnedDirectoryCreation TryCreateWindows(
        string parentPath,
        string childName,
        bool requireDirectoryDeleteAccess)
    {
        var parentHandle = OpenDirectoryWindows(parentPath, openReparsePoint: true);
        try
        {
            EnsureWindowsParentIsNotReparsePoint(parentHandle, parentPath);
            ExclusiveDirectoryCreator.InvokeBeforeCreateHook(Path.Join(parentPath, childName));
            var status = CreateRelativeWindows(
                parentHandle,
                childName,
                directory: true,
                hiddenFile: false,
                requireDirectoryDeleteAccess,
                out var rawHandle);
            if (status == StatusObjectNameCollision)
            {
                return new PinnedDirectoryCreation(
                    parentHandle,
                    directoryHandle: null,
                    parentPath,
                    childName,
                    created: false,
                    parentFollowsVisibleFinalLink: false);
            }
            if (status < 0)
            {
                throw CreateNtException(status, parentPath, childName);
            }

            return new PinnedDirectoryCreation(
                parentHandle,
                new SafeFileHandle(rawHandle, ownsHandle: true),
                parentPath,
                childName,
                created: true,
                parentFollowsVisibleFinalLink: false);
        }
        catch
        {
            parentHandle.Dispose();
            throw;
        }
    }

    private static PinnedDirectoryCreation TryCreateUnix(
        string parentPath,
        string childName)
    {
        var parentHandle = OpenDirectoryUnix(parentPath, noFollow: true);
        SafeFileHandle? directoryHandle = null;
        try
        {
            ExclusiveDirectoryCreator.InvokeBeforeCreateHook(Path.Join(parentPath, childName));
            var parentFd = parentHandle.DangerousGetHandle().ToInt32();
            if (MkdirAt(parentFd, childName, UnixDirectoryMode) != 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == UnixAlreadyExists)
                {
                    return new PinnedDirectoryCreation(
                        parentHandle,
                        directoryHandle: null,
                        parentPath,
                        childName,
                        created: false,
                        parentFollowsVisibleFinalLink: false);
                }

                throw new Win32Exception(
                    error,
                    $"Could not create the requested directory beneath '{parentPath}'.");
            }

            directoryHandle = OpenDirectoryAtUnix(parentHandle, childName);
            var created = new PinnedDirectoryCreation(
                parentHandle,
                directoryHandle,
                parentPath,
                childName,
                created: true,
                parentFollowsVisibleFinalLink: false);
            directoryHandle = null;
            var visibility = created.ProbeVisiblePathMatch();
            if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                created.Dispose();
                throw new IOException(
                    "The newly created directory is temporarily unavailable before it can be pinned.");
            }
            if (visibility != RegistrationPublicationMatchOutcome.Match)
            {
                created.Dispose();
                throw new InvalidOperationException(
                    "The newly created directory changed before it could be pinned.");
            }

            return created;
        }
        catch
        {
            directoryHandle?.Dispose();
            parentHandle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle CreateRelativeFileWindows(
        SafeFileHandle directoryHandle,
        string fileName,
        bool hiddenFile = true)
    {
        var status = CreateRelativeWindows(
            directoryHandle,
            fileName,
            directory: false,
            hiddenFile,
            requireDirectoryDeleteAccess: false,
            out var rawHandle);
        if (status == StatusObjectNameCollision)
        {
            throw new InvalidOperationException(
                "A pinned relative file unexpectedly already exists.");
        }
        if (status < 0)
        {
            throw CreateNtException(status, "pinned directory", fileName);
        }

        return new SafeFileHandle(rawHandle, ownsHandle: true);
    }

    private static SafeFileHandle CreateRelativeFileUnix(
        SafeFileHandle directoryHandle,
        string fileName)
    {
        var flags = GetUnixWriteFlags();
        var fd = OpenAt(
            directoryHandle.DangerousGetHandle().ToInt32(),
            fileName,
            flags,
            UnixFileMode);
        if (fd >= 0)
        {
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }

        var error = Marshal.GetLastWin32Error();
        if (error == UnixAlreadyExists)
        {
            throw new InvalidOperationException(
                "A pinned relative file unexpectedly already exists.");
        }

        throw new Win32Exception(error, "Could not create a pinned relative file.");
    }

}
