using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "PinnedDirectoryCreationPathTests")]
[Trait("Category", "Infrastructure")]
public sealed class PinnedDirectoryCreationPathTests : BaseTests
{
    [Fact]
    public void PinnedEntryPoints_RejectForeignSyntaxBeforeNativeNormalization()
    {
        var foreignPath = OperatingSystem.IsWindows()
            ? "/listenarr/foreign-root"
            : @"C:\Listenarr\foreign-root";

        Assert.Throws<ArgumentException>(() =>
            PinnedDirectoryCreation.OpenPinnedBoundary(foreignPath));
        Assert.Throws<ArgumentException>(() =>
            PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                foreignPath,
                createMissing: false));
    }
}
