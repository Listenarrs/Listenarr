using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FileSystemVolumeResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileSystemVolumeResolverTests : BaseTests
{
    [Fact]
    public void Compare_NonexistentDestinationBelowSameBoundary_ReportsSameVolume()
    {
        var root = FileService.GetTempDirectory("volume-resolver-same-volume");
        var source = Path.Join(root, "source");
        Directory.CreateDirectory(source);
        var destination = Path.Join(root, "not-created", "Book");
        var resolver = new FileSystemVolumeResolver();

        var result = resolver.Compare(source, destination);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.True(result.SameVolume);
    }

    [CrossVolumeFact]
    public void Compare_ProvisionedCrossVolumeBoundary_ReportsDifferentVolume()
    {
        var source = FileService.GetTempDirectory("volume-resolver-cross-volume");
        var destination = Environment.GetEnvironmentVariable(
            CrossVolumeFactAttribute.DestinationPathEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "A real cross-volume destination was not provided.");
        var resolver = new FileSystemVolumeResolver();

        var result = resolver.Compare(source, destination);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.False(result.SameVolume);
    }
}
