using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverDirectoryCopyPathIdentityTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverDirectoryCopyPathIdentityTests : BaseTests
{
    [Fact]
    [Trait("Method", "DurableRecoveryArtifactNameComparer")]
    [Trait("Scenario", "CaseDistinctRecoveryArtifactsRemainDistinct")]
    public void DurableRecoveryArtifactNameComparer_CaseDistinctRecoveryArtifactsRemainDistinct()
    {
        var names = new HashSet<string>(FileMover.DurableRecoveryArtifactNameComparer)
        {
            "source.claim",
            "Source.Claim"
        };

        Assert.Equal(2, names.Count);
    }

    [Fact]
    [Trait("Method", "DurablePathEvidenceEquals")]
    [Trait("Scenario", "WindowsCaseDistinctPathsRemainDistinct")]
    public void DurablePathEvidenceEquals_WindowsCaseDistinctPathsRemainDistinct()
    {
        Assert.True(FileMover.DurablePathEvidenceEquals(
            @"c:\Library\Book",
            @"C:\Library\Book",
            FileSystemPathSyntax.Windows));
        Assert.False(FileMover.DurablePathEvidenceEquals(
            @"C:\Library\Book",
            @"C:\Library\book",
            FileSystemPathSyntax.Windows));
    }

    [Fact]
    [Trait("Method", "DirectoryCopySnapshotPathComparer")]
    [Trait("Scenario", "CaseDistinctSnapshotEntriesRemainDistinct")]
    public void DirectoryCopySnapshotPathComparer_CaseDistinctSnapshotEntriesRemainDistinct()
    {
        var comparer = FileMover.DirectoryCopySnapshotPathComparer;
        var paths = new HashSet<string>(comparer)
        {
            Path.Join("Disc", "Track.m4b"),
            Path.Join("disc", "track.m4b")
        };

        Assert.Equal(2, paths.Count);
        Assert.False(comparer.Equals(
            Path.Join("Disc", "Track.m4b"),
            Path.Join("disc", "track.m4b")));
    }
}
