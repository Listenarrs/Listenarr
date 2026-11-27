using Listenarr.Infrastructure.Library.Files;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Files;

// The fixture is one second of generated silence, so it holds no recording. It was made with
//   ffmpeg -fflags +bitexact -f lavfi -i anullsrc=r=22050:cl=mono -t 1 -c:a aac -flags:a +bitexact
//     -b:a 16k -map_metadata -1 -metadata title="The Time Machine" -metadata artist="H. G. Wells"
//     -f ipod the-time-machine-silence.m4b
// and carries no ASIN, which is what lets each test prove the tag it reads back was written here.
[Trait("Name", "TagLibAudioTagWriterRoundTripTests")]
[Trait("Category", "Infrastructure")]
public sealed class TagLibAudioTagWriterRoundTripTests : BaseTests
{
    private const string FixtureName = "the-time-machine-silence.m4b";
    private const string Asin = "B007RPQWCG";

    [Fact]
    public void Fixture_BeforeAnyWrite_CarriesNoAsin()
    {
        var path = CopyFixture("fixture-untagged");

        Assert.Null(ReadAsin(path));
    }

    [Fact]
    public async Task WriteAsinTagAsync_ByPath_AsinSurvivesSaveAndReopen()
    {
        var path = CopyFixture("round-trip-path");
        var before = ReadAudioShape(path);
        var writer = new TagLibAudioTagWriter(Mock.Of<ILogger<TagLibAudioTagWriter>>());

        await writer.WriteAsinTagAsync(path, Asin);

        Assert.Equal(Asin, ReadAsin(path));
        Assert.Equal(before, ReadAudioShape(path));
    }

    [Fact]
    public async Task WriteAsinTagAsync_ThroughWriteAuthorizedLease_AsinSurvivesSaveAndReopen()
    {
        var path = CopyFixture("round-trip-lease");
        var before = ReadAudioShape(path);
        var writer = new TagLibAudioTagWriter(Mock.Of<ILogger<TagLibAudioTagWriter>>());

        using (var lease = PinnedAudiobookFileRegistrationLease.Open(
            path,
            supportsMetadataWrite: true))
        {
            // Fail loudly rather than skip when this storage cannot authorize a metadata write.
            Assert.True(lease.HasDurablePhysicalObjectIdentity);
            Assert.True(lease.SupportsMetadataWrite);

            await writer.WriteAsinTagAsync(lease, Asin);
        }

        Assert.Equal(Asin, ReadAsin(path));
        Assert.Equal(before, ReadAudioShape(path));
    }

    [Fact]
    public async Task WriteTagsAsync_AsinAndCoverArt_BothSurviveOneSaveAndReopen()
    {
        var path = CopyFixture("round-trip-cover");
        var before = ReadAudioShape(path);
        var cover = new AudioCoverArt(CoverBytes(0x11, png: true), "image/png");

        await WriteThroughLeaseAsync(path, Asin, cover);

        Assert.Equal(Asin, ReadAsin(path));
        var picture = Assert.Single(ReadPictures(path));
        Assert.Equal("image/png", picture.MimeType);
        Assert.Equal(cover.Data, picture.Data.Data);
        Assert.Equal(before, ReadAudioShape(path));
    }

    [Fact]
    public async Task WriteTagsAsync_CoverArtWithoutAsin_EmbedsTheCoverAndLeavesTheAsinUnset()
    {
        var path = CopyFixture("round-trip-cover-only");

        await WriteThroughLeaseAsync(path, asin: null, new AudioCoverArt(CoverBytes(0x22), "image/jpeg"));

        Assert.Null(ReadAsin(path));
        Assert.Equal("image/jpeg", Assert.Single(ReadPictures(path)).MimeType);
    }

    [Fact]
    public async Task WriteTagsAsync_FileAlreadyCarriesACover_ReplacesItOnDisk()
    {
        var path = CopyFixture("round-trip-cover-replace");
        await WriteThroughLeaseAsync(path, asin: null, new AudioCoverArt(CoverBytes(0x33, png: true), "image/png"));
        var replacement = new AudioCoverArt(CoverBytes(0x44), "image/jpeg");

        await WriteThroughLeaseAsync(path, asin: null, replacement);

        var picture = Assert.Single(ReadPictures(path));
        Assert.Equal("image/jpeg", picture.MimeType);
        Assert.Equal(replacement.Data, picture.Data.Data);
    }

    [Fact]
    public async Task WriteTagsAsync_AsinOnlyOnAFileWithACover_KeepsTheExistingCover()
    {
        // No artwork was fetched (setting off, or the fetch failed), so the file's own cover
        // must not be touched on the way past.
        var path = CopyFixture("round-trip-cover-kept");
        var original = new AudioCoverArt(CoverBytes(0x55, png: true), "image/png");
        await WriteThroughLeaseAsync(path, asin: null, original);

        await WriteThroughLeaseAsync(path, Asin, coverArt: null);

        Assert.Equal(Asin, ReadAsin(path));
        Assert.Equal(original.Data, Assert.Single(ReadPictures(path)).Data.Data);
    }

    [Fact]
    public async Task WriteTagsAsync_WebpCoverOnAnMpeg4File_IsNotEmbeddedButTheAsinIs()
    {
        // The MPEG-4 cover atom has format flags for JPEG, PNG, BMP and GIF only. A WebP
        // picture is stored with no format flag and reads back as an opaque blob that players
        // do not show, so it is better left out than written as if it worked.
        var path = CopyFixture("round-trip-cover-webp");
        byte[] webp = [0x52, 0x49, 0x46, 0x46, 0x1A, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x4C, 0, 0, 0, 0];

        await WriteThroughLeaseAsync(path, Asin, new AudioCoverArt(webp, "image/webp"));

        Assert.Equal(Asin, ReadAsin(path));
        Assert.Empty(ReadPictures(path));
    }

    [Fact]
    public async Task WriteTagsAsync_WhenNothingCouldBeApplied_DoesNotSaveOrClaimAWrite()
    {
        // A WebP cover with no ASIN on an MPEG-4 file leaves nothing to write.
        var path = CopyFixture("round-trip-nothing-applied");
        var before = File.GetLastWriteTimeUtc(path);
        var bytesBefore = await File.ReadAllBytesAsync(path);
        var logger = new CapturingLogger();
        byte[] webp = [0x52, 0x49, 0x46, 0x46, 0x1A, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x4C, 0, 0, 0, 0];

        await WriteThroughLeaseAsync(path, asin: null, new AudioCoverArt(webp, "image/webp"), logger);

        Assert.DoesNotContain(logger.Messages, message => message.Contains("Wrote tags"));
        Assert.Contains(logger.Messages, message => message.Contains("left unchanged"));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(path));
    }

    private sealed class CapturingLogger : ILogger<TagLibAudioTagWriter>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static async Task WriteThroughLeaseAsync(
        string path,
        string? asin,
        AudioCoverArt? coverArt,
        ILogger<TagLibAudioTagWriter>? logger = null)
    {
        var writer = new TagLibAudioTagWriter(logger ?? Mock.Of<ILogger<TagLibAudioTagWriter>>());
        using var lease = PinnedAudiobookFileRegistrationLease.Open(
            path,
            supportsMetadataWrite: true);
        Assert.True(lease.SupportsMetadataWrite);

        await writer.WriteTagsAsync(lease, asin, coverArt);
    }

    // An MPEG-4 file records the image format, not the writer's MIME type, and works it out
    // from the bytes when reading, so the fill sits behind a real format signature. The
    // distinguishing fill is what proves which picture came back.
    private static byte[] CoverBytes(byte fill, bool png = false) =>
        png
            ? [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, fill, fill, fill, fill]
            : [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, fill, 0xFF, 0xD9];

    private static TagLib.IPicture[] ReadPictures(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Tag.Pictures;
    }

    private string CopyFixture(string directoryName)
    {
        var directory = FileService.GetTempDirectory(directoryName);
        var path = Path.Join(directory, "book.m4b");
        File.Copy(TestUtils.GetAudioDataPath(FixtureName), path);
        return path;
    }

    private static string? ReadAsin(string path)
    {
        using var file = TagLib.File.Create(path);
        var appleTag = (TagLib.Mpeg4.AppleTag?)file.GetTag(TagLib.TagTypes.Apple, create: false);
        return appleTag?.GetDashBox("com.apple.iTunes", "ASIN");
    }

    private static (TimeSpan Duration, string Codec) ReadAudioShape(string path)
    {
        using var file = TagLib.File.Create(path);
        return (file.Properties.Duration, file.Properties.Description);
    }
}
