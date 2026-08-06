using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "DirectoryObjectIdentityResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class DirectoryObjectIdentityResolverTests : BaseTests
{
    [Fact]
    public async Task ResolveAsync_IsStableWithoutFilesystemMarker()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-stable");
        var resolver = new DirectoryObjectIdentityResolver();

        var first = await resolver.ResolveAsync(directory);
        var second = await resolver.ResolveAsync(directory);

        Assert.True(first.IsAvailable, first.UnavailableReason);
        Assert.Equal(ManagedDirectoryIdentity.CurrentVersion, first.Version);
        Assert.Equal(first, second);
        Assert.False(File.Exists(Path.Join(
            directory,
            ManagedDirectoryEnrollment.FileName)));
    }

    [Fact]
    public async Task ResolveExistingAsync_LegacyVersionTwoValue_ValidatesFromNativeGenerationWithoutMarker()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-existing-v2");
        const string nativeIdentity = "stable-native-generation";
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: static _ => nativeIdentity);
        var legacyPersisted = ManagedDirectoryIdentity.Create(
            Guid.NewGuid().ToString("N"),
            nativeIdentity);

        var existing = await resolver.ResolveExistingAsync(
            directory,
            ManagedDirectoryIdentity.CurrentVersion,
            legacyPersisted);

        Assert.True(existing.IsAvailable, existing.UnavailableReason);
        Assert.Equal(legacyPersisted, existing.Value);
        Assert.False(File.Exists(Path.Join(
            directory,
            ManagedDirectoryEnrollment.FileName)));
    }

    [Fact]
    public async Task ResolveExistingAsync_DifferentNativeGeneration_IsUnavailable()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-recreated");
        var nativeIdentity = "generation-a";
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: _ => nativeIdentity);
        var first = await resolver.ResolveAsync(directory);
        Assert.True(first.IsAvailable, first.UnavailableReason);

        nativeIdentity = "generation-b";
        var existing = await resolver.ResolveExistingAsync(
            directory,
            first.Version!.Value,
            first.Value!);

        Assert.False(existing.IsAvailable);
        Assert.Contains(
            "physical identity",
            existing.UnavailableReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveExistingAsync_ForeignMarkerCannotAuthorizeDifferentNativeGeneration()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-foreign-marker");
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: static _ => "native-current");
        var expected = ManagedDirectoryIdentity.Create(
            Guid.NewGuid().ToString("N"),
            "native-original");
        await File.WriteAllTextAsync(
            Path.Join(directory, ManagedDirectoryEnrollment.FileName),
            "{\"version\":1,\"token\":\"00000000000000000000000000000000\"}");

        var existing = await resolver.ResolveExistingAsync(
            directory,
            ManagedDirectoryIdentity.CurrentVersion,
            expected);

        Assert.False(existing.IsAvailable);
        Assert.True(File.Exists(Path.Join(
            directory,
            ManagedDirectoryEnrollment.FileName)));
    }

    [Fact]
    public async Task UpgradeLegacyAsync_MatchingNativeIdentity_ProducesMarkerlessVersionTwo()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-upgrade");
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: static _ => "legacy-native");

        var upgraded = await resolver.UpgradeLegacyAsync(
            directory,
            legacyVersion: 1,
            legacyValue: "legacy-native");
        var existing = await resolver.ResolveExistingAsync(
            directory,
            upgraded.Version!.Value,
            upgraded.Value!);

        Assert.True(upgraded.IsAvailable, upgraded.UnavailableReason);
        Assert.Equal(ManagedDirectoryIdentity.CurrentVersion, upgraded.Version);
        Assert.Equal(upgraded, existing);
        Assert.False(File.Exists(Path.Join(
            directory,
            ManagedDirectoryEnrollment.FileName)));
    }

    [Fact]
    public async Task UpgradeLegacyAsync_MismatchedNativeIdentity_FailsClosedWithoutMarker()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-upgrade-mismatch");
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: static _ => "current-native");

        var upgraded = await resolver.UpgradeLegacyAsync(
            directory,
            legacyVersion: 1,
            legacyValue: "different-native");

        Assert.False(upgraded.IsAvailable);
        Assert.False(File.Exists(Path.Join(
            directory,
            ManagedDirectoryEnrollment.FileName)));
    }

    [Fact]
    public async Task ResolveAsync_ForeignPersistedSyntax_FailsClosedBeforeNativeProbeOrMarkerWrite()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-foreign-syntax");
        var nativeProbeCount = 0;
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: _ =>
            {
                nativeProbeCount++;
                return "should-not-be-probed";
            });
        var foreignPath = OperatingSystem.IsWindows()
            ? "/" + Path.GetRelativePath(Path.GetPathRoot(directory)!, directory)
                .Replace('\\', '/')
            : @"C:\Listenarr\foreign-root";
        var expectedForeignSyntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Unix
            : FileSystemPathSyntax.Windows;
        var expected = ManagedDirectoryIdentity.CreateMarkerless("expected-native");

        var resolution = await resolver.ResolveAsync(foreignPath);
        var existing = await resolver.ResolveExistingAsync(
            foreignPath,
            ManagedDirectoryIdentity.CurrentVersion,
            expected);
        var legacy = await resolver.UpgradeLegacyAsync(
            foreignPath,
            legacyVersion: 1,
            legacyValue: "persisted-foreign-native-identity");

        foreach (var candidate in new[] { resolution, existing, legacy })
        {
            Assert.False(candidate.IsAvailable);
            Assert.Contains(
                $"{expectedForeignSyntax} filesystem syntax",
                candidate.UnavailableReason,
                StringComparison.Ordinal);
        }
        Assert.Equal(0, nativeProbeCount);
        Assert.False(File.Exists(Path.Join(
            directory,
            ManagedDirectoryEnrollment.FileName)));
    }

    [Fact]
    public async Task ResolveAsync_ReturnsUnavailableForMissingDirectory()
    {
        var directory = Path.Join(
            FileService.GetTempPath(),
            $"missing-directory-{Guid.NewGuid():N}");
        var resolution = await new DirectoryObjectIdentityResolver()
            .ResolveAsync(directory);

        Assert.False(resolution.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(resolution.UnavailableReason));
    }

    [LinuxFact]
    public async Task ResolveExistingAsync_ImmediateNativeDeleteRecreate_DetectsGenerationChange()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-native-recreate");
        var resolver = new DirectoryObjectIdentityResolver();
        var first = await resolver.ResolveAsync(directory);
        Assert.True(first.IsAvailable, first.UnavailableReason);

        Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var existing = await resolver.ResolveExistingAsync(
            directory,
            first.Version!.Value,
            first.Value!);

        Assert.False(existing.IsAvailable);
    }
}
