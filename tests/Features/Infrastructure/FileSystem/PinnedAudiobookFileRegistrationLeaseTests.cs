using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "PinnedAudiobookFileRegistrationLeaseTests")]
[Trait("Category", "FileSystem")]
public sealed class PinnedAudiobookFileRegistrationLeaseTests : BaseTests
{
    [LinuxFact]
    public async Task CreatePinnedPathOnly_PublicPathReplaced_KeepsOriginalMetadataHandleWithoutDurableAuthority()
    {
        var parentPath = FileService.GetTempDirectory(
            "registration-lease-pinned-path-only");
        var publicPath = await FileService.GetFileAsync(
            parentPath,
            "book.m4b",
            "original generation");
        var displacedPath = Path.Join(parentPath, "book-original.m4b");
        using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            parentPath,
            createMissing: false);
        var file = parent.OpenExistingFileForStableRead(Path.GetFileName(publicPath));
        using var lease = PinnedAudiobookFileRegistrationLease.CreatePinnedPathOnly(
            file,
            publicPath);

        File.Move(publicPath, displacedPath);
        await File.WriteAllTextAsync(publicPath, "replacement generation");

        Assert.False(lease.HasDurablePhysicalObjectIdentity);
        Assert.False(lease.MatchesCurrentPublication());
        Assert.False(lease.MatchesPhysicalObjectIdentity("linux-generation:00000000:00000000:0000000000000000:gen:00000000"));
        Assert.Equal(
            "original generation",
            await File.ReadAllTextAsync(lease.MetadataPath));
        Assert.Equal(
            "replacement generation",
            await File.ReadAllTextAsync(publicPath));
    }

    [LinuxFact]
    public async Task ProbeCurrentPublication_ParentReplaced_DetectsPublicPathMismatch()
    {
        var parent = FileService.GetTempDirectory(
            "registration-lease-parent-replacement");
        var displacedParent = parent + "-displaced";
        var publicPath = await FileService.GetFileAsync(
            parent,
            "book.m4b",
            "original generation");
        using var lease = PinnedAudiobookFileRegistrationLease.Open(publicPath);

        Directory.Move(parent, displacedParent);
        Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(
            Path.Join(parent, "book.m4b"),
            "replacement generation");

        Assert.Equal(
            RegistrationPublicationMatchOutcome.Mismatch,
            lease.ProbeCurrentPublication());
        Assert.False(lease.MatchesCurrentPublication());
        Assert.Equal(
            "original generation",
            await File.ReadAllTextAsync(lease.MetadataPath));
        Assert.Equal(
            "replacement generation",
            await File.ReadAllTextAsync(Path.Join(parent, "book.m4b")));
    }

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

    [Fact]
    public async Task OpenMetadataWriteStream_IsReadableSoATagLibraryCanParseWhatItRewrites()
    {
        var parent = FileService.GetTempDirectory(
            "registration-lease-metadata-write-access");
        var publicPath = await FileService.GetFileAsync(
            parent,
            "book.m4b",
            "original generation");
        using var lease = PinnedAudiobookFileRegistrationLease.Open(publicPath);

        using var stream = lease.OpenMetadataWriteStream();

        // TagLib reads the existing box structure back through the write stream before it
        // saves, so a write-only handle throws NotSupportedException mid-parse and the tag is
        // never written. The import still reports success, which is why this needs asserting
        // rather than observing.
        Assert.True(stream.CanWrite);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);

        var buffer = new byte[stream.Length];
        Assert.Equal(buffer.Length, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal("original generation", System.Text.Encoding.UTF8.GetString(buffer));
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
