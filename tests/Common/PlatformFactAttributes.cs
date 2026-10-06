namespace Listenarr.Tests.Common;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "This test requires native Windows behavior.";
        }
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires native Linux behavior.";
        }
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "This test requires native Windows behavior.";
        }
    }
}

public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires native Linux behavior.";
        }
    }
}

public sealed class ReadOnlyBindMountFactAttribute : FactAttribute
{
    public const string LibraryPathEnvironmentVariable =
        "LISTENARR_READONLY_LIBRARY_PATH";

    public ReadOnlyBindMountFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires a native Linux read-only bind mount.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                LibraryPathEnvironmentVariable)))
        {
            Skip = "The native test runner did not provide a read-only library bind mount.";
        }
    }
}

public sealed class CrossVolumeFactAttribute : FactAttribute
{
    public const string DestinationPathEnvironmentVariable =
        "LISTENARR_CROSS_VOLUME_DESTINATION_PATH";

    public CrossVolumeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                DestinationPathEnvironmentVariable)))
        {
            Skip = "The native test runner did not provide a destination on another filesystem or volume.";
        }
    }
}

public sealed class NetworkStorageTheoryAttribute : TheoryAttribute
{
    public const string PathEnvironmentVariable =
        "LISTENARR_NETWORK_STORAGE_PATH";

    public NetworkStorageTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires a native Linux network filesystem mount.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                PathEnvironmentVariable)))
        {
            Skip = "The native test runner did not provide a network filesystem mount.";
        }
    }
}

public sealed class ForeignOwnedNetworkStorageFactAttribute : FactAttribute
{
    public const string SourcePathEnvironmentVariable =
        "LISTENARR_NETWORK_FOREIGN_SOURCE_PATH";

    public ForeignOwnedNetworkStorageFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires a native Linux network filesystem mount.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                NetworkStorageTheoryAttribute.PathEnvironmentVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                SourcePathEnvironmentVariable)))
        {
            Skip = "The native test runner did not provide a network mount and foreign-owned source.";
        }
    }
}

public sealed class DirectoryLinkFactAttribute : FactAttribute
{
    public DirectoryLinkFactAttribute()
    {
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.DirectorySymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}

public sealed class DirectoryLinkTheoryAttribute : TheoryAttribute
{
    public DirectoryLinkTheoryAttribute()
    {
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.DirectorySymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}

public sealed class FileLinkFactAttribute : FactAttribute
{
    public FileLinkFactAttribute()
    {
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.FileSymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}

public sealed class FileLinkTheoryAttribute : TheoryAttribute
{
    public FileLinkTheoryAttribute()
    {
        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.FileSymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}

public sealed class LinuxDirectoryAndFileLinkFactAttribute : FactAttribute
{
    public LinuxDirectoryAndFileLinkFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires native Linux behavior.";
            return;
        }

        var decision = NativeTestCapabilityPolicy.GetExecutionDecision(
            NativeTestCapability.DirectorySymbolicLinks,
            NativeTestCapability.FileSymbolicLinks);
        if (!decision.ShouldRun)
        {
            Skip = decision.SkipReason;
        }
    }
}

/// <summary>
/// A fact that needs two writable directories whose only common ancestor is the filesystem
/// root. The test output tree and the system temp directory usually qualify, but a checkout
/// under the temp directory, or a TMPDIR inside the workspace, leaves no such pair, and on
/// Windows they can sit on different drives entirely. Skipping says so rather than failing a
/// run for a property of the machine.
/// </summary>
public sealed class DisjointFilesystemRootsFactAttribute : FactAttribute
{
    public DisjointFilesystemRootsFactAttribute()
    {
        var buildOutput = Path.GetFullPath(AppContext.BaseDirectory);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!string.Equals(
                Path.GetPathRoot(buildOutput),
                Path.GetPathRoot(temp),
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "The build output and the temp directory are on different filesystem roots, "
                + "so a batch spanning both has no common directory at all.";
            return;
        }

        if (string.Equals(FirstSegment(buildOutput), FirstSegment(temp), StringComparison.OrdinalIgnoreCase))
        {
            Skip = "The build output and the temp directory share a first path segment, so this "
                + "fixture cannot build a batch whose only common ancestor is the filesystem root.";
        }
    }

    private static string FirstSegment(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path[root.Length..]
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?? string.Empty;
    }
}
