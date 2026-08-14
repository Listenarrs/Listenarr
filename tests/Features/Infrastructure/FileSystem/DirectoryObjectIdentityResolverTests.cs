using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "DirectoryObjectIdentityResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class DirectoryObjectIdentityResolverTests : BaseTests
{
    [Fact]
    public void LinuxInodeGenerationIoctl_UsesNativeLongWidthForEachRequest()
    {
        var bindingFlags = System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var ioctl64 = typeof(PinnedDirectoryCreation).GetMethod(
            "IoctlGetVersion64",
            bindingFlags);
        var ioctl32 = typeof(PinnedDirectoryCreation).GetMethod(
            "IoctlGetVersion32",
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
    }

    [Fact]
    public async Task ResolveExistingAsync_MatchesPersistedV1AgainstNonPreferredNativeCandidate()
    {
        var directory = FileService.GetTempDirectory(
            "directory-object-identity-v1-candidate-compatibility");
        const string persistedNativeIdentity = "legacy-v1-native-identity";
        var persisted = ManagedDirectoryIdentity.CreateMarkerless(persistedNativeIdentity);
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityCandidatesResolver: static _ =>
                ["newly-available-stronger-identity", persistedNativeIdentity]);

        var existing = await resolver.ResolveExistingAsync(
            directory,
            ManagedDirectoryIdentity.CurrentVersion,
            persisted);

        Assert.True(existing.IsAvailable, existing.UnavailableReason);
        Assert.Equal(persisted, existing.Value);
    }

    [LinuxFact]
    public async Task ResolveExistingAsync_LegacyLinuxV1Identity_IsClassifiedAsWeakEvidence()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-legacy-v1");
        const string legacyNative =
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc";
        var persisted = ManagedDirectoryIdentity.CreateMarkerless(legacyNative);
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityCandidatesResolver: static _ =>
            [
                "linux-generation:00000008:00000001:0000000000001234:gen:00000001",
                legacyNative + ":gen:00000001"
            ]);

        var existing = await resolver.ResolveExistingAsync(
            directory,
            ManagedDirectoryIdentity.CurrentVersion,
            persisted);

        Assert.False(existing.IsAvailable);
        Assert.Equal(
            DirectoryObjectIdentityFailureKind.LegacyWeakIdentity,
            existing.FailureKind);
    }

    [LinuxFact]
    public async Task ResolveAsync_UnixAccessDeniedNativeError_IsClassifiedAsAccessDenied()
    {
        var directory = FileService.GetTempDirectory(
            "directory-object-identity-unix-access-denied");
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: static _ =>
                throw new System.ComponentModel.Win32Exception(13, "Permission denied"));

        var resolution = await resolver.ResolveAsync(directory);

        Assert.False(resolution.IsAvailable);
        Assert.Equal(
            DirectoryObjectIdentityFailureKind.AccessDenied,
            resolution.FailureKind);
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
        foreach (var candidate in new[] { resolution, existing })
        {
            Assert.False(candidate.IsAvailable);
            Assert.Contains(
                $"{expectedForeignSyntax} filesystem syntax",
                candidate.UnavailableReason,
                StringComparison.Ordinal);
        }
        Assert.Equal(0, nativeProbeCount);
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

    [Fact]
    public void LinuxIdentity_WithBirthTime_PrefersStrongGenerationAndRetainsMergedV1Candidate()
    {
        var candidates = PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
            deviceMajor: 8,
            deviceMinor: 1,
            inode: 0x1234,
            hasBirthTime: true,
            birthTimeSeconds: 0x5678,
            birthTimeNanoseconds: 0x9abc,
            generationIdentities: ["gen:00000001"]);

        Assert.Equal(
            "linux-generation:00000008:00000001:0000000000001234:gen:00000001",
            candidates[0]);
        Assert.Contains(
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc:gen:00000001",
            candidates);
        Assert.DoesNotContain(
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc",
            candidates);
    }

    [Fact]
    public void LinuxIdentity_MultipleGenerationCapabilities_EmitEveryVerificationCandidate()
    {
        var candidates = PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
            deviceMajor: 8,
            deviceMinor: 1,
            inode: 0x1234,
            hasBirthTime: false,
            birthTimeSeconds: 0,
            birthTimeNanoseconds: 0,
            generationIdentities: ["fh:00000001:deadbeef", "gen:00000002"]);

        Assert.Equal(
            [
                "linux-generation:00000008:00000001:0000000000001234:fh:00000001:deadbeef",
                "linux-generation:00000008:00000001:0000000000001234:gen:00000002"
            ],
            candidates);
    }

    [Fact]
    public void LinuxIdentity_WithoutBirthTime_UsesStrongAlternativeGenerationEvidence()
    {
        var identity = PinnedDirectoryCreation.CreateLinuxObjectIdentityFromEvidence(
            deviceMajor: 8,
            deviceMinor: 1,
            inode: 0x1234,
            hasBirthTime: false,
            birthTimeSeconds: 0,
            birthTimeNanoseconds: 0,
            generationIdentity: "fh:00000001:deadbeef");

        Assert.Equal(
            "linux-generation:00000008:00000001:0000000000001234:fh:00000001:deadbeef",
            identity);
    }

    [Fact]
    public void LinuxRawIdentity_AugmentedV1Token_CanFallBackOnlyToItsBirthTimePrefix()
    {
        Assert.True(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc:gen:00000001",
            out var prefix));
        Assert.Equal(
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc",
            prefix);

        Assert.False(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            "linux-generation:00000008:00000001:0000000000001234:fh:deadbeef",
            out _));
        Assert.False(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc:future:deadbeef",
            out _));
        Assert.False(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc:gen:nothex00",
            out _));
        Assert.False(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc:fh:00000001:abc",
            out _));
        Assert.False(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            "linux:nothex00:00000001:0000000000001234:0000000000005678:00009abc:gen:00000001",
            out _));
    }

    [LinuxFact]
    public void LinuxPersistedIdentityEquivalence_RequiresSameStrongGenerationEvidence()
    {
        const string birthTimeIdentity =
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc";
        const string augmented = birthTimeIdentity + ":gen:00000001";
        const string strong =
            "linux-generation:00000008:00000001:0000000000001234:gen:00000001";

        Assert.False(PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
            birthTimeIdentity,
            augmented));
        Assert.True(PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
            augmented,
            strong));
        Assert.True(PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
            strong,
            augmented));
        Assert.False(PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
            strong,
            "linux-generation:00000008:00000001:0000000000001234:fh:00000001:deadbeef"));
    }

    [Fact]
    public void LinuxIdentity_WithoutAnyGenerationEvidence_FailsClosed()
    {
        var exception = Assert.Throws<PlatformNotSupportedException>(() =>
            PinnedDirectoryCreation.CreateLinuxObjectIdentityFromEvidence(
                deviceMajor: 8,
                deviceMinor: 1,
                inode: 0x1234,
                hasBirthTime: false,
                birthTimeSeconds: 0,
                birthTimeNanoseconds: 0,
                generationIdentity: null));

        Assert.Contains(
            "durable file handle or inode generation",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(22)]
    [InlineData(25)]
    [InlineData(38)]
    [InlineData(95)]
    public void LinuxOptionalGenerationProbe_CommonCapabilityErrors_AreNonFatal(int error)
    {
        Assert.True(PinnedDirectoryCreation.IsUnavailableLinuxGenerationProbeError(error));
    }

    [Fact]
    public void LinuxOptionalGenerationProbe_UnexpectedIoError_RemainsFatal()
    {
        Assert.False(PinnedDirectoryCreation.IsUnavailableLinuxGenerationProbeError(5));
    }

    [LinuxFact]
    public async Task ResolveAsync_LinuxEnosys_IsClassifiedAsIdentityUnsupported()
    {
        var directory = FileService.GetTempDirectory("directory-object-identity-enosys");
        var resolver = new DirectoryObjectIdentityResolver(
            nativeIdentityResolver: static _ =>
                throw new System.ComponentModel.Win32Exception(38));

        var resolution = await resolver.ResolveAsync(directory);

        Assert.False(resolution.IsAvailable);
        Assert.Equal(
            DirectoryObjectIdentityFailureKind.IdentityUnsupported,
            resolution.FailureKind);
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
