using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "DockerStorageCapabilityContractTests")]
[Trait("Category", "Infrastructure")]
public sealed class DockerStorageCapabilityContractTests : BaseTests
{
    [Fact]
    public void Restart_StrongFileHandleThenBirthTimeOnly_FailsClosedRatherThanDowngradingAuthority()
    {
        var firstRun = PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
            deviceMajor: 8,
            deviceMinor: 1,
            inode: 0x1234,
            hasBirthTime: true,
            birthTimeSeconds: 0x5678,
            birthTimeNanoseconds: 0x9abc,
            generationIdentities: ["fh:00000001:01020304"]);

        Assert.StartsWith("linux-generation:", firstRun[0], StringComparison.Ordinal);
        Assert.Throws<PlatformNotSupportedException>(() =>
            PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
                deviceMajor: 8,
                deviceMinor: 1,
                inode: 0x1234,
                hasBirthTime: true,
                birthTimeSeconds: 0x5678,
                birthTimeNanoseconds: 0x9abc,
                generationIdentities: []));
    }

    [Fact]
    public void Restart_GenerationOnlyThenFileHandleAlsoAvailable_RetainsOriginalGenerationCandidate()
    {
        var firstRun = PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
            deviceMajor: 8,
            deviceMinor: 1,
            inode: 0x1234,
            hasBirthTime: false,
            birthTimeSeconds: 0,
            birthTimeNanoseconds: 0,
            generationIdentities: ["gen:01020304"]);
        var secondRun = PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
            deviceMajor: 8,
            deviceMinor: 1,
            inode: 0x1234,
            hasBirthTime: false,
            birthTimeSeconds: 0,
            birthTimeNanoseconds: 0,
            generationIdentities:
            [
                "fh:00000001:01020304",
                "gen:01020304"
            ]);

        Assert.Single(firstRun);
        Assert.Contains(firstRun[0], secondRun);
        Assert.Contains(
            secondRun,
            candidate => candidate.EndsWith(
                ":fh:00000001:01020304",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Mount_BirthTimeWithoutStrongGeneration_FailsClosed()
    {
        var exception = Assert.Throws<PlatformNotSupportedException>(() =>
            PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
                deviceMajor: 8,
                deviceMinor: 1,
                inode: 0x1234,
                hasBirthTime: true,
                birthTimeSeconds: 0x5678,
                birthTimeNanoseconds: 0x9abc,
                generationIdentities: []));

        Assert.Contains(
            "durable file handle or inode generation",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mount_NoBirthTimeAndNoStrongGeneration_FailsClosed()
    {
        var exception = Assert.Throws<PlatformNotSupportedException>(() =>
            PinnedDirectoryCreation.CreateLinuxObjectIdentityCandidatesFromEvidence(
                deviceMajor: 8,
                deviceMinor: 1,
                inode: 0x1234,
                hasBirthTime: false,
                birthTimeSeconds: 0,
                birthTimeNanoseconds: 0,
                generationIdentities: []));

        Assert.Contains(
            "durable file handle or inode generation",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restart_MergedV1AugmentedToken_RecognizesOnlyKnownHistoricalSuffixGrammar()
    {
        const string birthTime =
            "linux:00000008:00000001:0000000000001234:0000000000005678:00009abc";

        Assert.True(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            birthTime + ":gen:01020304",
            out var generationPrefix));
        Assert.Equal(birthTime, generationPrefix);

        Assert.True(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            birthTime + ":fh:00000001:01020304",
            out var fileHandlePrefix));
        Assert.Equal(birthTime, fileHandlePrefix);

        Assert.False(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            birthTime + ":gen:nothex",
            out _));
        Assert.False(PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
            birthTime + ":future:01020304",
            out _));
    }

    [Fact]
    public void CaseProbe_SameUniqueRegularFileOnSameMount_CanProveInsensitiveLookup()
    {
        var exact = new PinnedDirectoryCreation.LinuxCaseAliasEvidence(
            IsDirectory: false,
            IsRegularFile: true,
            LinkCount: 1,
            HasLinkCount: true,
            MountId: 42,
            HasMountId: true);
        var alternate = exact;

        var outcome = PinnedDirectoryCreation.ClassifySameLinuxCaseAlias(
            exact,
            alternate,
            out var reason);

        Assert.Equal(
            PinnedDirectoryCreation.LinuxCaseAliasProbeOutcome.Insensitive,
            outcome);
        Assert.Null(reason);
    }

    [Fact]
    public void CaseProbe_SameInodeAcrossDifferentMounts_DoesNotClaimInsensitiveLookup()
    {
        var exact = new PinnedDirectoryCreation.LinuxCaseAliasEvidence(
            IsDirectory: true,
            IsRegularFile: false,
            LinkCount: 2,
            HasLinkCount: true,
            MountId: 42,
            HasMountId: true);
        var alternate = exact with { MountId = 43 };

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
    public void CaseProbe_MissingMountIdentity_RemainsInconclusive()
    {
        var exact = new PinnedDirectoryCreation.LinuxCaseAliasEvidence(
            IsDirectory: false,
            IsRegularFile: true,
            LinkCount: 1,
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
            reason,
            StringComparison.OrdinalIgnoreCase);
    }
}
