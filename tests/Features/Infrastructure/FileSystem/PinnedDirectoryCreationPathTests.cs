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

    [Fact]
    public void OpenPinnedHierarchyNoFollow_MissingExistingChild_DoesNotReportACreatedDirectory()
    {
        var missingDirectory = Path.Join(
            FileService.GetTempDirectory("pinned-missing-existing-child"),
            "absent");

        var exception = Record.Exception(() =>
            PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                missingDirectory,
                createMissing: false));

        Assert.NotNull(exception);
        Assert.DoesNotContain("newly created", exception.Message, StringComparison.Ordinal);
    }
}
