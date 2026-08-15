using System.Runtime.Versioning;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileSystemSemanticsResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileSystemSemanticsResolverTests : BaseTests
{
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
            Assert.Equal(before, Snapshot(parent));
            Assert.False(Directory.Exists(Path.Join(parent, "future")));
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
