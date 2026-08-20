using System.Runtime.InteropServices;

namespace Listenarr.Infrastructure.FileSystem;

internal static class UnixOpenFlags
{
    private const int LinuxWriteOnly = 1 << 0;
    private const int LinuxReadWrite = 1 << 1;
    private const int LinuxCreate = 1 << 6;
    private const int LinuxExclusive = 1 << 7;
    private const int LinuxNonBlocking = 1 << 11;
    private const int LinuxCloseOnExec = 1 << 19;

    private const int MacWriteOnly = 0x1;
    private const int MacReadWrite = 0x2;
    private const int MacNonBlocking = 0x4;
    private const int MacCreate = 0x200;
    private const int MacExclusive = 0x800;
    private const int MacNoFollow = 0x100;
    private const int MacDirectory = 0x100000;
    private const int MacCloseOnExec = 0x1000000;

    internal static int Directory(bool noFollow)
    {
        if (IsMacOSHost())
        {
            return MacDirectory
                | MacCloseOnExec
                | (noFollow ? MacNoFollow : 0);
        }

        var (directory, noFollowFlag) = GetCurrentLinuxDirectorySafetyFlags();
        return directory
            | LinuxCloseOnExec
            | (noFollow ? noFollowFlag : 0);
    }

    internal static int OpenReadNoFollow()
    {
        if (IsMacOSHost())
        {
            return MacNonBlocking | MacNoFollow | MacCloseOnExec;
        }

        var (_, noFollow) = GetCurrentLinuxDirectorySafetyFlags();
        return LinuxNonBlocking | noFollow | LinuxCloseOnExec;
    }

    internal static int CreateReadWriteExclusiveNoFollow()
    {
        if (IsMacOSHost())
        {
            return MacReadWrite
                | MacCreate
                | MacExclusive
                | MacNoFollow
                | MacCloseOnExec;
        }

        var (_, noFollow) = GetCurrentLinuxDirectorySafetyFlags();
        return LinuxReadWrite
            | LinuxCreate
            | LinuxExclusive
            | noFollow
            | LinuxCloseOnExec;
    }

    internal static int OpenWriteNoFollow()
    {
        if (IsMacOSHost())
        {
            return MacWriteOnly | MacNoFollow | MacCloseOnExec;
        }

        var (_, noFollow) = GetCurrentLinuxDirectorySafetyFlags();
        return LinuxWriteOnly | noFollow | LinuxCloseOnExec;
    }

    internal static int CreateWriteExclusiveNoFollow()
    {
        if (IsMacOSHost())
        {
            return MacWriteOnly
                | MacCreate
                | MacExclusive
                | MacNoFollow
                | MacCloseOnExec;
        }

        var (_, noFollow) = GetCurrentLinuxDirectorySafetyFlags();
        return LinuxWriteOnly
            | LinuxCreate
            | LinuxExclusive
            | noFollow
            | LinuxCloseOnExec;
    }

    internal static int OpenOrCreateReadWriteNoFollow()
    {
        if (IsMacOSHost())
        {
            return MacReadWrite | MacCreate | MacNoFollow | MacCloseOnExec;
        }

        var (_, noFollow) = GetCurrentLinuxDirectorySafetyFlags();
        return LinuxReadWrite | LinuxCreate | noFollow | LinuxCloseOnExec;
    }

    private static bool IsMacOSHost()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        EnsureMacOSArchitectureSupported(RuntimeInformation.ProcessArchitecture);
        return true;
    }

    internal static void EnsureMacOSArchitectureSupported(Architecture architecture)
    {
        if (architecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                $"Listenarr does not support macOS filesystem operations on process architecture '{architecture}'.");
        }
    }

    private static (int Directory, int NoFollow) GetCurrentLinuxDirectorySafetyFlags()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Unix open flags are supported only on released Linux and macOS targets.");
        }

        return GetLinuxDirectorySafetyFlags(RuntimeInformation.ProcessArchitecture);
    }

    internal static (int Directory, int NoFollow) GetLinuxDirectorySafetyFlags(
        Architecture architecture) => architecture switch
        {
            // Listenarr distributes Linux only for x64 and arm64. Keep the
            // supported process ABI set aligned with the release matrix instead
            // of silently extending filesystem safety guarantees to architectures
            // we do not build, test, or publish.
            Architecture.Arm64 => (1 << 14, 1 << 15),
            Architecture.X64 => (1 << 16, 1 << 17),
            _ => throw new PlatformNotSupportedException(
                $"Listenarr does not support Linux filesystem operations on process architecture '{architecture}'.")
        };
}
