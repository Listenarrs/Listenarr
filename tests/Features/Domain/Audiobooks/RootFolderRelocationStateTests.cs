using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Audiobooks;

[Trait("Name", "RootFolderRelocationStateTests")]
[Trait("Category", "Domain")]
public sealed class RootFolderRelocationStateTests : BaseTests
{
    [Fact]
    public void NewRoot_RequiresResolvedIdentityBeforeDestructiveWork()
    {
        var root = new RootFolder();

        Assert.Equal(FileSystemCaseSensitivityMode.Auto, root.CaseSensitivityMode);
        Assert.Equal(FileSystemCaseSensitivity.Unknown, root.ResolvedCaseSensitivity);
        Assert.Equal(PathIdentityState.Unavailable, root.PathIdentityState);
        Assert.Null(root.PathIdentityKey);
    }

    [Fact]
    public void PersistedSemantics_AmbiguousDoubleSlashPath_RequiresFilesystemContext()
    {
        var root = new RootFolder
        {
            Path = "//server/share/Library",
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
            PathIdentityState = PathIdentityState.Valid
        };

        Assert.Null(RootFolderPathSemantics.ResolvePersisted(root));
    }

    [Fact]
    public void PersistedSemantics_ExplicitModeOverridesStaleResolvedSensitivity()
    {
        var root = new RootFolder
        {
            Path = "C:\\Library",
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
            PathIdentityState = PathIdentityState.Valid
        };

        var persisted = Assert.IsType<PersistedRootFolderPathSemantics>(
            RootFolderPathSemantics.ResolvePersisted(root));

        Assert.Equal(FileSystemCaseSensitivity.Sensitive, persisted.Semantics.CaseSensitivity);
        Assert.False(persisted.DetectAmbiguousCaseMatches);
    }

    [Fact]
    public void PersistedSemantics_AutoUnavailableIdentityFailsClosedDespiteStaleResolvedSensitivity()
    {
        var root = new RootFolder
        {
            Path = "C:\\Library",
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
            PathIdentityState = PathIdentityState.Unavailable
        };

        var persisted = Assert.IsType<PersistedRootFolderPathSemantics>(
            RootFolderPathSemantics.ResolvePersisted(root));

        Assert.Equal(FileSystemCaseSensitivity.Sensitive, persisted.Semantics.CaseSensitivity);
        Assert.True(persisted.DetectAmbiguousCaseMatches);
    }

    [Fact]
    public void NewRelocation_HoldsPendingPathWithoutChangingRoot()
    {
        var relocation = new RootFolderRelocation
        {
            SourcePath = "/library",
            TargetPath = "/new-library"
        };

        Assert.Equal(RootFolderRelocationStatus.Pending, relocation.Status);
        Assert.Equal("/new-library", relocation.TargetPath);
    }

}
