using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Contracts;

[Trait("Name", "AudioCoverArtTests")]
[Trait("Category", "Application")]
public sealed class AudioCoverArtTests : BaseTests
{
    [Fact]
    public void FromBytes_NamesTheImageTypeFromItsLeadingBytes()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var webp = new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 };

        Assert.Equal("image/jpeg", AudioCoverArt.FromBytes(jpeg)!.MimeType);
        Assert.Equal("image/png", AudioCoverArt.FromBytes(png)!.MimeType);
        Assert.Equal("image/webp", AudioCoverArt.FromBytes(webp)!.MimeType);
    }

    [Fact]
    public void FromBytes_RefusesAnythingItCannotName()
    {
        // An indexer or a metadata source can answer an image request with an HTML error
        // page. Embedding that under image/jpeg produces a file whose cover silently fails
        // to render, which is harder to diagnose than no cover at all.
        var html = "<!DOCTYPE html><html><body>404"u8.ToArray();

        Assert.Null(AudioCoverArt.FromBytes(html));
        Assert.Null(AudioCoverArt.FromBytes(null));
        Assert.Null(AudioCoverArt.FromBytes([0xFF, 0xD8]));
    }
}
