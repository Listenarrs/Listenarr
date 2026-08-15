using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "PinnedAudiobookFileRegistrationLeaseTests")]
[Trait("Category", "FileSystem")]
public sealed class PinnedAudiobookFileRegistrationLeaseTests : BaseTests
{
    [LinuxFact]
    public async Task OpenMetadataWriteStream_PublicPathReplaced_DoesNotOpenReplacementGeneration()
    {
        var parent = FileService.GetTempDirectory(
            "registration-lease-metadata-replacement");
        var publicPath = await FileService.GetFileAsync(
            parent,
            "book.m4b",
            "original generation");
        var displacedPath = Path.Join(parent, "book-original.m4b");
        using var lease = PinnedAudiobookFileRegistrationLease.Open(publicPath);

        File.Move(publicPath, displacedPath);
        await File.WriteAllTextAsync(publicPath, "replacement generation");

        Assert.Throws<InvalidOperationException>(() =>
            lease.OpenMetadataWriteStream());
        Assert.Equal(
            "replacement generation",
            await File.ReadAllTextAsync(publicPath));
        Assert.Equal(
            "original generation",
            await File.ReadAllTextAsync(displacedPath));
    }

    [WindowsFact]
    public async Task StableRegistrationLease_BlocksPublicPathReplacementUntilDisposed()
    {
        var parent = FileService.GetTempDirectory(
            "registration-lease-metadata-replacement-windows");
        var publicPath = await FileService.GetFileAsync(
            parent,
            "book.m4b",
            "original generation");
        var displacedPath = Path.Join(parent, "book-original.m4b");

        using (var lease = PinnedAudiobookFileRegistrationLease.Open(publicPath))
        {
            Assert.ThrowsAny<IOException>(() =>
                File.Move(publicPath, displacedPath));
            Assert.Equal(
                "original generation",
                await File.ReadAllTextAsync(publicPath));
        }

        File.Move(publicPath, displacedPath);
        Assert.Equal(
            "original generation",
            await File.ReadAllTextAsync(displacedPath));
    }
}
