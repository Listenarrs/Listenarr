using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    // Windows error codes returned when the underlying volume cannot honor a durable
    // directory barrier. SMB / network-mapped drives reject FlushFileBuffers (and the
    // barrier-open) with ERROR_INVALID_FUNCTION; some SMB configurations surface
    // ERROR_NOT_SUPPORTED instead. These are "this filesystem does not implement the
    // operation" signals, not transient or corruption errors, so a durable flush that
    // fails with them can be safely skipped: the file is already moved and registered
    // when the barrier runs, and the barrier is a crash-safety guarantee rather than a
    // correctness requirement.
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;

    internal static bool IsUnsupportedDurableBarrierError(int win32Error) =>
        win32Error is ErrorInvalidFunction or ErrorNotSupported;

    /// <summary>
    /// Observes a durable filesystem barrier that was skipped because the volume does not
    /// support it (see <see cref="IsUnsupportedDurableBarrierError"/>). Wired to the logger
    /// by <see cref="FileMover"/> so the reduced crash-safety is visible, while the import or
    /// rename that triggered it still succeeds instead of being reported as failed.
    /// </summary>
    internal static Action<DurableBarrierDegrade>? DurableBarrierDegradeObserver { get; set; }

    internal readonly record struct DurableBarrierDegrade(string Site, string Target, int Win32Error);

    private static void ObserveDurableBarrierDegrade(string site, string target, int win32Error)
    {
        try
        {
            DurableBarrierDegradeObserver?.Invoke(new DurableBarrierDegrade(site, target, win32Error));
        }
        catch
        {
            // Diagnostics must never alter control flow.
        }
    }

    private static void FlushHandleToDisk(
        SafeFileHandle handle,
        string description)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!FlushFileBuffers(handle))
            {
                var error = Marshal.GetLastWin32Error();
                if (IsUnsupportedDurableBarrierError(error))
                {
                    ObserveDurableBarrierDegrade("FlushHandleToDisk/FlushFileBuffers", description, error);
                    return;
                }
                throw new PlatformNotSupportedException(
                    $"The filesystem could not durably flush {description} (Windows error {error}).");
            }
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            const int fullFileSystemSync = 51;
            if (FcntlGetPath(
                    handle.DangerousGetHandle().ToInt32(),
                    fullFileSystemSync,
                    IntPtr.Zero) != 0)
            {
                throw new PlatformNotSupportedException(
                    $"The filesystem could not provide a full durable flush for {description} (errno {Marshal.GetLastWin32Error()}).");
            }
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            while (FSync(handle.DangerousGetHandle().ToInt32()) != 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 4)
                {
                    continue;
                }
                throw new PlatformNotSupportedException(
                    $"The filesystem could not durably flush {description} (errno {error}).");
            }
            return;
        }

        throw new PlatformNotSupportedException(
            "Durable filesystem barriers are supported only on Windows, Linux, and macOS.");
    }

    private static void FlushDirectoryPathToDisk(
        SafeFileHandle pinnedHandle,
        string path,
        bool followVisibleFinalLink)
    {
        if (!OperatingSystem.IsWindows())
        {
            FlushHandleToDisk(pinnedHandle, $"directory '{path}'");
            return;
        }

        using var flushHandle = CreateFileWindows(
            path,
            GenericRead | GenericWrite | Synchronize,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics
                | (followVisibleFinalLink ? 0u : FileFlagOpenReparsePoint),
            IntPtr.Zero);
        if (flushHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (IsUnsupportedDurableBarrierError(error))
            {
                ObserveDurableBarrierDegrade("FlushDirectoryPathToDisk/CreateFile", path, error);
                return;
            }
            throw new PlatformNotSupportedException(
                $"The filesystem could not open a durable directory barrier for '{path}' (Windows error {error}).");
        }
        if (!followVisibleFinalLink)
        {
            EnsureWindowsParentIsNotReparsePoint(flushHandle, path);
        }
        if (!HandlesIdentifySameDirectory(pinnedHandle, flushHandle))
        {
            throw new InvalidOperationException(
                "The directory changed while its durability barrier was opened.");
        }
        if (!FlushFileBuffers(flushHandle))
        {
            var error = Marshal.GetLastWin32Error();
            if (IsUnsupportedDurableBarrierError(error))
            {
                ObserveDurableBarrierDegrade("FlushDirectoryPathToDisk/FlushFileBuffers", path, error);
                return;
            }
            throw new PlatformNotSupportedException(
                $"The filesystem could not durably flush directory '{path}' (Windows error {error}).");
        }
    }
}
