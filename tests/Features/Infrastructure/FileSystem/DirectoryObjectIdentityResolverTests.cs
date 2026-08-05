using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "DirectoryObjectIdentityResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class DirectoryObjectIdentityResolverTests : BaseTests
{
    [Fact]
    public async Task ResolveAsync_IsStableForSameEnrolledDirectory()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-stable");
        var resolver = new DirectoryObjectIdentityResolver();

        var first = await resolver.ResolveAsync(directory);
        var second = await resolver.ResolveExistingAsync(directory);

        Assert.True(first.IsAvailable, first.UnavailableReason);
        Assert.Equal(ManagedDirectoryIdentity.CurrentVersion, first.Version);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Value, second.Value);
        Assert.True(File.Exists(Path.Join(
            directory,
            ManagedDirectoryEnrollment.FileName)));
    }

    [Fact]
    public async Task ResolveExistingAsync_RecreatedPathWithReusedNativeIdentity_IsUnavailable()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-recreated");
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: static _ => "simulated-reused-native-identity");
        var first = await resolver.ResolveAsync(directory);
        Assert.True(first.IsAvailable, first.UnavailableReason);

        Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var existing = await resolver.ResolveExistingAsync(directory);
        var reenrolled = await resolver.ResolveAsync(directory);

        Assert.False(existing.IsAvailable);
        Assert.Contains(
            "enrollment",
            existing.UnavailableReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(reenrolled.IsAvailable, reenrolled.UnavailableReason);
        Assert.NotEqual(first.Value, reenrolled.Value);
    }

    [Fact]
    public async Task ResolveExistingAsync_CopiedEnrollmentMarkerWithDifferentNativeIdentity_IsUnavailable()
    {
        var source = FileService.GetTempDirectory("directory-object-identity-source");
        var replacement = FileService.GetTempDirectory("directory-object-identity-replacement");
        var sourceResolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: anchor =>
                anchor.FullPath.EndsWith("source", StringComparison.Ordinal)
                    ? "native-source"
                    : "native-replacement");
        var sourceIdentity = await sourceResolver.ResolveAsync(source);
        Assert.True(sourceIdentity.IsAvailable, sourceIdentity.UnavailableReason);
        File.Copy(
            Path.Join(source, ManagedDirectoryEnrollment.FileName),
            Path.Join(replacement, ManagedDirectoryEnrollment.FileName));

        var replacementIdentity = await sourceResolver.ResolveExistingAsync(replacement);

        Assert.False(replacementIdentity.IsAvailable);
        Assert.Contains(
            "physical directory",
            replacementIdentity.UnavailableReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpgradeLegacyAsync_MatchingNativeIdentity_EnrollsVersionTwo()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-upgrade");
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: static _ => "legacy-native");

        var upgraded = await resolver.UpgradeLegacyAsync(
            directory,
            legacyVersion: 1,
            legacyValue: "legacy-native");
        var existing = await resolver.ResolveExistingAsync(directory);

        Assert.True(upgraded.IsAvailable, upgraded.UnavailableReason);
        Assert.True(upgraded.EnrollmentCreated);
        Assert.Equal(ManagedDirectoryIdentity.CurrentVersion, upgraded.Version);
        Assert.True(existing.IsAvailable, existing.UnavailableReason);
        Assert.False(existing.EnrollmentCreated);
        Assert.Equal(upgraded.Version, existing.Version);
        Assert.Equal(upgraded.Value, existing.Value);
    }

    [Fact]
    public async Task UpgradeLegacyAsync_MismatchedNativeIdentity_FailsClosedWithoutEnrollment()
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
    public async Task RetireEnrollmentAsync_ExactCreatedGeneration_RemovesMarker()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-retire");
        var resolver = new DirectoryObjectIdentityResolver();
        var enrolled = await resolver.ResolveAsync(directory);
        Assert.True(enrolled.IsAvailable, enrolled.UnavailableReason);
        Assert.True(enrolled.EnrollmentCreated);

        await resolver.RetireEnrollmentAsync(
            directory,
            enrolled.Version!.Value,
            enrolled.Value!);

        Assert.False(File.Exists(Path.Join(
            directory,
            ManagedDirectoryEnrollment.FileName)));
        var existing = await resolver.ResolveExistingAsync(directory);
        Assert.False(existing.IsAvailable);
    }

    [Fact]
    public async Task RetireEnrollmentAsync_MismatchedExpectedGeneration_PreservesMarker()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-retire-mismatch");
        var resolver = new DirectoryObjectIdentityResolver();
        var enrolled = await resolver.ResolveAsync(directory);
        Assert.True(enrolled.IsAvailable, enrolled.UnavailableReason);
        var markerPath = Path.Join(directory, ManagedDirectoryEnrollment.FileName);
        var original = await File.ReadAllTextAsync(markerPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.RetireEnrollmentAsync(
                directory,
                enrolled.Version!.Value,
                "listenarr-directory-v2:00000000000000000000000000000000:"
                    + new string('0', 64)));

        Assert.Equal(original, await File.ReadAllTextAsync(markerPath));
    }

    [Fact]
    public async Task ResolveAsync_ForeignPersistedSyntax_FailsClosedBeforeNativeProbeOrEnrollment()
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

        var resolution = await resolver.ResolveAsync(foreignPath);
        var existing = await resolver.ResolveExistingAsync(foreignPath);
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
    public async Task ResolveExistingAsync_ImmediateNativeDeleteRecreate_DoesNotRetainEnrollment()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-native-recreate");
        var resolver = new DirectoryObjectIdentityResolver();
        var first = await resolver.ResolveAsync(directory);
        Assert.True(first.IsAvailable, first.UnavailableReason);

        Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var existing = await resolver.ResolveExistingAsync(directory);

        Assert.False(existing.IsAvailable);
    }
}
