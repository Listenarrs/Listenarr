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

        using (var lease = PinnedAudiobookFileRegistrationLease.Open(path))
        {
            // Fail loudly rather than skip when this storage cannot authorize a metadata write.
            Assert.True(lease.HasDurablePhysicalObjectIdentity);

            await writer.WriteAsinTagAsync(lease, Asin);
        }

        Assert.Equal(Asin, ReadAsin(path));
        Assert.Equal(before, ReadAudioShape(path));
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
