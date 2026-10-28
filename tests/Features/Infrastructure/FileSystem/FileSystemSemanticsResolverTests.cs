using System.Runtime.Versioning;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileSystemSemanticsResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileSystemSemanticsResolverTests : BaseTests
{
    [Fact]
    public void LinuxFilesystemFlagsIoctl_UsesNativeLongWidthForEachRequest()
    {
        var bindingFlags = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var ioctl64 = typeof(FileSystemSemanticsResolver).GetMethod(
            "IoctlUnix64",
            bindingFlags);
        var ioctl32 = typeof(FileSystemSemanticsResolver).GetMethod(
            "IoctlUnix32",
            bindingFlags);

        Assert.NotNull(ioctl64);
        Assert.NotNull(ioctl32);
        Assert.Equal(
            typeof(long).MakeByRefType(),
            ioctl64.GetParameters()[2].ParameterType);
        Assert.Equal(
            typeof(int).MakeByRefType(),
            ioctl32.GetParameters()[2].ParameterType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("relative\0path")]
    public async Task ResolveAsync_RejectsInvalidOrRelativePath(string path)
    {
        var resolver = new FileSystemSemanticsResolver();

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await resolver.ResolveAsync(path, FileSystemCaseSensitivityMode.Auto));
    }

    [Fact]
    public async Task ExplicitOverride_ResolvesMissingPathWithoutFilesystemWrites()
    {
        var parent = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-explicit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var missingPath = Path.Join(parent, "future", "books");
            var before = Snapshot(parent);

            var resolution = await new FileSystemSemanticsResolver().ResolveAsync(
                missingPath,
                FileSystemCaseSensitivityMode.Sensitive);

            Assert.Equal(PathIdentityState.Valid, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Sensitive,
                resolution.Semantics.CaseSensitivity);
            Assert.Equal(
                FileSystemSemanticsEvidenceKind.Authoritative,
                resolution.EvidenceKind);
            Assert.True(resolution.HasDurableMutationSemanticsAuthority);
            Assert.Equal(before, Snapshot(parent));
            Assert.False(Directory.Exists(Path.Join(parent, "future")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task Auto_InaccessibleExistingBoundary_DoesNotFallBackToParentSemantics()
    {
        var parent = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-unavailable-" + Guid.NewGuid().ToString("N"));
        var boundary = Path.Join(parent, "Book");
        Directory.CreateDirectory(boundary);
        try
        {
            var resolver = new FileSystemSemanticsResolver(
                _ => new FileSystemSemanticsResolver.LinuxFilesystemFlagsProbe(
                    Success: false,
                    Flags: 0,
                    ErrorCode: 25),
                path => string.Equals(path, boundary, StringComparison.Ordinal)
                    ? throw new UnauthorizedAccessException(
                        "Injected boundary access failure.")
                    : File.GetAttributes(path));

            var resolution = await resolver.ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Unavailable, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Unknown,
                resolution.Semantics.CaseSensitivity);
            Assert.Equal(Path.GetFullPath(boundary), resolution.CanonicalPath);
            Assert.Contains(
                "Injected boundary access failure",
                resolution.Reason ?? string.Empty,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task Auto_ExistingBoundary_DoesNotCreateOrModifyEntries()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        await File.WriteAllTextAsync(Path.Join(boundary, "existing.txt"), "unchanged");
        try
        {
            var before = Snapshot(boundary);

            var resolution = await new FileSystemSemanticsResolver().ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            AssertReadOnlyResolutionForCurrentHost(resolution);
            Assert.Equal(before, Snapshot(boundary));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(
                Path.Join(boundary, "existing.txt")));
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    [Fact]
    public async Task Auto_MissingDescendant_UsesExistingBoundaryWithoutCreatingIt()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-future-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        await File.WriteAllTextAsync(Path.Join(boundary, "existing.txt"), "unchanged");
        try
        {
            var requested = Path.Join(boundary, "future", "books");
            var before = Snapshot(boundary);

            var resolution = await new FileSystemSemanticsResolver().ResolveAsync(
                requested,
                FileSystemCaseSensitivityMode.Auto);

            AssertReadOnlyResolutionForCurrentHost(resolution);
            Assert.Equal(Path.GetFullPath(requested), resolution.CanonicalPath);
            Assert.Equal(before, Snapshot(boundary));
            Assert.False(Directory.Exists(Path.Join(boundary, "future")));
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    [DirectoryLinkFact]
    public async Task Auto_LinkedBoundary_DoesNotWriteThroughLink()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-link-" + Guid.NewGuid().ToString("N"));
        var physical = Path.Join(root, "physical");
        var linked = Path.Join(root, "linked");
        Directory.CreateDirectory(physical);
        await File.WriteAllTextAsync(Path.Join(physical, "existing.txt"), "unchanged");
        Directory.CreateSymbolicLink(linked, physical);
        try
        {
            var before = Snapshot(physical);

            var resolution = await new FileSystemSemanticsResolver().ResolveAsync(
                linked,
                FileSystemCaseSensitivityMode.Auto);

            AssertReadOnlyResolutionForCurrentHost(resolution);
            Assert.Equal(Path.GetFullPath(linked), resolution.CanonicalPath);
            Assert.Equal(before, Snapshot(physical));
        }
        finally
        {
            Directory.Delete(linked);
            Directory.Delete(root, true);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Auto_ReadOnlyBoundary_ResolvesWithoutRequiringWritePermission()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        await File.WriteAllTextAsync(Path.Join(boundary, "existing.txt"), "unchanged");
        var originalMode = File.GetUnixFileMode(boundary);
        try
        {
            File.SetUnixFileMode(
                boundary,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var before = Snapshot(boundary);

            var resolution = await new FileSystemSemanticsResolver().ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Valid, resolution.State);
            Assert.NotEqual(
                FileSystemCaseSensitivity.Unknown,
                resolution.Semantics.CaseSensitivity);
            Assert.Equal(before, Snapshot(boundary));
        }
        finally
        {
            File.SetUnixFileMode(boundary, originalMode);
            Directory.Delete(boundary, true);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Auto_WhenFilesystemFlagsLackCasefoldFlag_UsesReadOnlyAliasProbe()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-flags-without-casefold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        await File.WriteAllTextAsync(Path.Join(boundary, "Existing.txt"), "unchanged");
        try
        {
            var before = Snapshot(boundary);
            var resolver = new FileSystemSemanticsResolver(
                _ => new FileSystemSemanticsResolver.LinuxFilesystemFlagsProbe(
                    Success: true,
                    Flags: 0,
                    ErrorCode: 0));

            var resolution = await resolver.ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Valid, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Sensitive,
                resolution.Semantics.CaseSensitivity);
            Assert.Equal(before, Snapshot(boundary));
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Auto_WhenFilesystemFlagsReportCasefold_IsInsensitiveWithoutEntryProbe()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-casefold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        try
        {
            var before = Snapshot(boundary);
            var resolver = new FileSystemSemanticsResolver(
                _ => new FileSystemSemanticsResolver.LinuxFilesystemFlagsProbe(
                    Success: true,
                    Flags: 0x40000000,
                    ErrorCode: 0));

            var resolution = await resolver.ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Valid, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Insensitive,
                resolution.Semantics.CaseSensitivity);
            Assert.Equal(before, Snapshot(boundary));
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Auto_WhenExtFamilyFlagsLackCasefoldAndDirectoryIsEmpty_IsSensitive()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-empty-ext-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        try
        {
            var resolver = new FileSystemSemanticsResolver(
                _ => new FileSystemSemanticsResolver.LinuxFilesystemFlagsProbe(
                    Success: true,
                    Flags: 0,
                    ErrorCode: 0,
                    FileSystemType: 0x0000ef53L));

            var resolution = await resolver.ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Valid, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Sensitive,
                resolution.Semantics.CaseSensitivity);
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Auto_WhenKnownCasefoldFlagFilesystemLacksCasefold_IsAuthoritativelySensitive()
    {
        var fileSystemTypes = new long[]
        {
            0x0000ef53L, // ext family
            0xf2f52010L, // F2FS
            0x01021994L, // tmpfs
            0xca451a4eL // bcachefs
        };

        foreach (var fileSystemType in fileSystemTypes)
        {
            var boundary = Path.Join(
                Path.GetTempPath(),
                "filesystem-semantics-authoritative-flags-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(boundary);
            try
            {
                var resolver = new FileSystemSemanticsResolver(
                    _ => new FileSystemSemanticsResolver.LinuxFilesystemFlagsProbe(
                        Success: true,
                        Flags: 0,
                        ErrorCode: 0,
                        FileSystemType: fileSystemType));

                var resolution = await resolver.ResolveAsync(
                    boundary,
                    FileSystemCaseSensitivityMode.Auto);

                Assert.Equal(PathIdentityState.Valid, resolution.State);
                Assert.Equal(
                    FileSystemCaseSensitivity.Sensitive,
                    resolution.Semantics.CaseSensitivity);
                Assert.Equal(
                    FileSystemSemanticsEvidenceKind.Authoritative,
                    resolution.EvidenceKind);
                Assert.True(resolution.HasDurableMutationSemanticsAuthority);
            }
            finally
            {
                Directory.Delete(boundary, true);
            }
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Auto_WhenFilesystemFlagsLackCasefoldAndDirectoryIsEmpty_RemainsUnavailable()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-empty-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        try
        {
            var resolver = new FileSystemSemanticsResolver(
                _ => new FileSystemSemanticsResolver.LinuxFilesystemFlagsProbe(
                    Success: true,
                    Flags: 0,
                    ErrorCode: 0));

            var resolution = await resolver.ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Unavailable, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Unknown,
                resolution.Semantics.CaseSensitivity);
            Assert.Contains(
                "did not positively report case-insensitive lookup",
                resolution.Reason ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "Select Sensitive or Insensitive explicitly",
                resolution.Reason ?? string.Empty,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Auto_WhenFilesystemFlagsIoctlIsUnsupported_UsesReadOnlyExistingEntryProbe()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        await File.WriteAllTextAsync(Path.Join(boundary, "Existing.txt"), "unchanged");
        try
        {
            var before = Snapshot(boundary);
            var resolver = new FileSystemSemanticsResolver(
                _ => new FileSystemSemanticsResolver.LinuxFilesystemFlagsProbe(
                    Success: false,
                    Flags: 0,
                    ErrorCode: 25));

            var resolution = await resolver.ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Valid, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Sensitive,
                resolution.Semantics.CaseSensitivity);
            Assert.Equal(
                FileSystemSemanticsEvidenceKind.BehavioralObservation,
                resolution.EvidenceKind);
            Assert.False(resolution.HasDurableMutationSemanticsAuthority);
            Assert.Equal(before, Snapshot(boundary));
            Assert.Equal(
                "unchanged",
                await File.ReadAllTextAsync(Path.Join(boundary, "Existing.txt")));
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    [Fact]
    public void LinuxCaseAliasClassifier_SameInodeAcrossDifferentMounts_IsSensitive()
    {
        var exact = new PinnedDirectoryCreation.LinuxCaseAliasEvidence(
            IsDirectory: true,
            IsRegularFile: false,
            LinkCount: 2,
            HasLinkCount: true,
            MountId: 10,
            HasMountId: true);
        var alternate = exact with { MountId = 11 };

        var outcome = PinnedDirectoryCreation.ClassifySameLinuxCaseAlias(
            exact,
            alternate,
            out var reason);

        Assert.Equal(
            PinnedDirectoryCreation.LinuxCaseAliasProbeOutcome.Sensitive,
            outcome);
        Assert.Null(reason);
    }

    [Fact]
    public void LinuxCaseAliasClassifier_MultiplyLinkedRegularFile_IsInconclusive()
    {
        var exact = new PinnedDirectoryCreation.LinuxCaseAliasEvidence(
            IsDirectory: false,
            IsRegularFile: true,
            LinkCount: 2,
            HasLinkCount: true,
            MountId: 10,
            HasMountId: true);

        var outcome = PinnedDirectoryCreation.ClassifySameLinuxCaseAlias(
            exact,
            exact,
            out var reason);

        Assert.Equal(
            PinnedDirectoryCreation.LinuxCaseAliasProbeOutcome.RetryCandidate,
            outcome);
        Assert.Contains(
            "alias-ambiguous",
            reason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinuxCaseAliasClassifier_SingleLinkedRegularFileOnSameMount_IsInsensitive()
    {
        var exact = new PinnedDirectoryCreation.LinuxCaseAliasEvidence(
            IsDirectory: false,
            IsRegularFile: true,
            LinkCount: 1,
            HasLinkCount: true,
            MountId: 10,
            HasMountId: true);

        var outcome = PinnedDirectoryCreation.ClassifySameLinuxCaseAlias(
            exact,
            exact,
            out var reason);

        Assert.Equal(
            PinnedDirectoryCreation.LinuxCaseAliasProbeOutcome.Insensitive,
            outcome);
        Assert.Null(reason);
    }

    [Fact]
    public void LinuxCaseAliasClassifier_MissingMountIdentity_IsInconclusive()
    {
        var exact = new PinnedDirectoryCreation.LinuxCaseAliasEvidence(
            IsDirectory: true,
            IsRegularFile: false,
            LinkCount: 2,
            HasLinkCount: true,
            MountId: 0,
            HasMountId: false);

        var outcome = PinnedDirectoryCreation.ClassifySameLinuxCaseAlias(
            exact,
            exact,
            out var reason);

        Assert.Equal(
            PinnedDirectoryCreation.LinuxCaseAliasProbeOutcome.RetryCandidate,
            outcome);
        Assert.Contains(
            "mount identity is unavailable",
            reason ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Auto_WhenCaseVariantsAreSeparateHardLinks_DoesNotMisclassifyInsensitive()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-hardlink-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        var exact = Path.Join(boundary, "Exact.txt");
        var alias = Path.Join(boundary, "exact.txt");
        await File.WriteAllTextAsync(exact, "unchanged");
        Assert.Equal(0, NativeFileMethods.CreateHardLinkUnix(exact, alias));
        try
        {
            var before = Snapshot(boundary);
            var resolver = new FileSystemSemanticsResolver(
                _ => new FileSystemSemanticsResolver.LinuxFilesystemFlagsProbe(
                    Success: false,
                    Flags: 0,
                    ErrorCode: 25));

            var resolution = await resolver.ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Unavailable, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Unknown,
                resolution.Semantics.CaseSensitivity);
            Assert.Contains(
                "existing-entry probe was inconclusive",
                resolution.Reason ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, Snapshot(boundary));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(exact));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(alias));
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Auto_WhenAllReadOnlyCaseProbesAreInconclusive_RemainsUnavailableWithoutWrites()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-inconclusive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        try
        {
            var before = Snapshot(boundary);
            var resolver = new FileSystemSemanticsResolver(
                _ => new FileSystemSemanticsResolver.LinuxFilesystemFlagsProbe(
                    Success: false,
                    Flags: 0,
                    ErrorCode: 25));

            var resolution = await resolver.ResolveAsync(
                boundary,
                FileSystemCaseSensitivityMode.Auto);

            Assert.Equal(PathIdentityState.Unavailable, resolution.State);
            Assert.Equal(
                FileSystemCaseSensitivity.Unknown,
                resolution.Semantics.CaseSensitivity);
            Assert.Contains(
                "existing-entry probe was inconclusive",
                resolution.Reason ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, Snapshot(boundary));
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    [Fact]
    public async Task Auto_RepeatedResolution_NeverPublishesProbeArtifacts()
    {
        var boundary = Path.Join(
            Path.GetTempPath(),
            "filesystem-semantics-repeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        try
        {
            var before = Snapshot(boundary);
            var resolver = new FileSystemSemanticsResolver();

            var first = await resolver.ResolveAsync(boundary);
            var second = await resolver.ResolveAsync(boundary);

            Assert.Equal(first.State, second.State);
            Assert.Equal(first.Semantics, second.Semantics);
            Assert.Equal(before, Snapshot(boundary));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(boundary),
                entry => Path.GetFileName(entry).StartsWith(
                    ".listenarr-",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(boundary, true);
        }
    }

    private static void AssertReadOnlyResolutionForCurrentHost(
        FileSystemSemanticsResolution resolution)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            Assert.NotEqual(
                FileSystemCaseSensitivity.Unknown,
                resolution.Semantics.CaseSensitivity);
            return;
        }

        Assert.Equal(PathIdentityState.Unavailable, resolution.State);
        Assert.Equal(
            FileSystemCaseSensitivity.Unknown,
            resolution.Semantics.CaseSensitivity);
        Assert.Contains(
            "Select Sensitive or Insensitive explicitly",
            resolution.Reason ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> Snapshot(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
}
