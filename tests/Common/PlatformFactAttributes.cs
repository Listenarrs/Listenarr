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

public sealed class NativeStorageIdentityFactAttribute : FactAttribute
{
    public const string PathEnvironmentVariable =
        "LISTENARR_NATIVE_STORAGE_TEST_PATH";
    public const string ExpectationEnvironmentVariable =
        "LISTENARR_NATIVE_STORAGE_IDENTITY_EXPECTATION";

    public NativeStorageIdentityFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires a native Linux storage mount.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                PathEnvironmentVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                ExpectationEnvironmentVariable)))
        {
            Skip = "The native test runner did not provide a storage mount and identity expectation.";
        }
    }
}

public sealed class NativeWeakStorageFactAttribute : FactAttribute
{
    public NativeWeakStorageFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires a native Linux weak-storage mount.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                NativeStorageIdentityFactAttribute.PathEnvironmentVariable))
            || !string.Equals(
                Environment.GetEnvironmentVariable(
                    NativeStorageIdentityFactAttribute.ExpectationEnvironmentVariable),
                "generic-fid",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "The native test runner did not provide a generic-FID weak-storage mount.";
        }
    }
}

public sealed class NativeStorageRemountFactAttribute : FactAttribute
{
    public const string PathEnvironmentVariable =
        NativeStorageIdentityFactAttribute.PathEnvironmentVariable;
    public const string StatePathEnvironmentVariable =
        "LISTENARR_NATIVE_STORAGE_IDENTITY_STATE_PATH";
    public const string PhaseEnvironmentVariable =
        "LISTENARR_NATIVE_STORAGE_IDENTITY_PHASE";
    public const string ExpectationEnvironmentVariable =
        NativeStorageIdentityFactAttribute.ExpectationEnvironmentVariable;

    public NativeStorageRemountFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires a native Linux storage mount.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                PathEnvironmentVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                StatePathEnvironmentVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                PhaseEnvironmentVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                ExpectationEnvironmentVariable)))
        {
            Skip = "The native test runner did not provide the storage remount fixture state and identity expectation.";
        }
    }
}

public sealed class NativeWeakStorageRemountFactAttribute : FactAttribute
{
    public NativeWeakStorageRemountFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "This test requires a native Linux weak-storage remount fixture.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                NativeStorageRemountFactAttribute.PathEnvironmentVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                NativeStorageRemountFactAttribute.StatePathEnvironmentVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                NativeStorageRemountFactAttribute.PhaseEnvironmentVariable))
            || !string.Equals(
                Environment.GetEnvironmentVariable(
                    NativeStorageRemountFactAttribute.ExpectationEnvironmentVariable),
                "generic-fid",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "The native test runner did not provide a generic-FID weak-storage remount fixture.";
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
