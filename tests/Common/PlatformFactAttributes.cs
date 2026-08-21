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
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires native Linux cross-volume storage.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                DestinationPathEnvironmentVariable)))
        {
            Skip = "The native test runner did not provide a destination on another filesystem.";
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
