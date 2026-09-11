using Listenarr.Infrastructure.Library.Files;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Files;

[Trait("Name", "TagLibAudioTagWriterTests")]
[Trait("Category", "Infrastructure")]
public sealed class TagLibAudioTagWriterTests : BaseTests
{
    [Fact]
    public async Task WriteTagsAsync_WithNothingToWrite_DoesNotTouchTheFile()
    {
        // The gate lives above this, in MetadataService, which passes a null cover when the
        // setting is off. If both are absent there is nothing to write, and opening a
        // multi-gigabyte file to save it unchanged is the cost worth avoiding.
        var lease = new Mock<IAudiobookFileRegistrationLease>(MockBehavior.Strict);
        var writer = new TagLibAudioTagWriter(Mock.Of<ILogger<TagLibAudioTagWriter>>());

        await writer.WriteTagsAsync(lease.Object, asin: null, coverArt: null);

        lease.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WriteAsinTagAsync_ScanOnlyLease_DoesNotRequestMetadataStreams()
    {
        var lease = new Mock<IAudiobookFileRegistrationLease>(MockBehavior.Strict);
        lease.SetupGet(candidate => candidate.HasDurablePhysicalObjectIdentity)
            .Returns(false);
        lease.SetupGet(candidate => candidate.PublicPath)
            .Returns("scan-only-book.m4b");
        var writer = new TagLibAudioTagWriter(
            Mock.Of<ILogger<TagLibAudioTagWriter>>());

        await writer.WriteAsinTagAsync(lease.Object, "B0TESTASIN");

        lease.VerifyGet(candidate => candidate.HasDurablePhysicalObjectIdentity, Times.Once);
        lease.VerifyGet(candidate => candidate.PublicPath, Times.Once);
        lease.VerifyNoOtherCalls();
    }

    [Fact]
    public void ApplyCoverArt_FileAlreadyCarriesACover_ReplacesItRatherThanAppending()
    {
        // The docstring on ApplyCoverArt makes this a product decision: a file that gains a
        // second cover shows whichever the player picks first, which reads as a bug to
        // whoever asked for the artwork to be corrected. Nothing asserted it.
        var existing = new TagLib.Picture(new TagLib.ByteVector([1, 2, 3, 4]))
        {
            Type = TagLib.PictureType.BackCover,
            MimeType = "image/jpeg",
            Description = "Old"
        };
        var tag = new TagLib.Id3v2.Tag
        {
            Pictures = [existing]
        };
        using var file = new StubTagLibFile(tag);
        var coverArt = new AudioCoverArt([9, 8, 7, 6, 5], "image/png");

        var wrote = TagLibAudioTagWriter.ApplyCoverArt(file, coverArt);

        Assert.True(wrote);
        var picture = Assert.Single(file.Tag.Pictures);
        Assert.Equal("image/png", picture.MimeType);
        Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        Assert.Equal(coverArt.Data, picture.Data.Data);
    }

    /// <summary>
    /// A TagLib.File that owns no bytes, so ApplyCoverArt can be driven without a parseable
    /// container on disk.
    /// </summary>
    private sealed class StubTagLibFile(TagLib.Tag tag) : TagLib.File(new EmptyFileAbstraction())
    {
        public override TagLib.Tag Tag => tag;

        public override TagLib.Properties Properties =>
            throw new NotSupportedException("The stub exposes no audio properties.");

        public override void Save() =>
            throw new NotSupportedException("The stub has nothing to save.");

        public override void RemoveTags(TagLib.TagTypes types) =>
            throw new NotSupportedException("The stub removes nothing.");

        public override TagLib.Tag? GetTag(TagLib.TagTypes type, bool create) =>
            type == tag.TagTypes ? tag : null;

        private sealed class EmptyFileAbstraction : IFileAbstraction
        {
            public string Name => "stub.mp3";

            public Stream ReadStream => Stream.Null;

            public Stream WriteStream => Stream.Null;

            public void CloseStream(Stream stream)
            {
            }
        }
    }
}
