using Listenarr.Infrastructure.Library.Files;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Files;

[Trait("Name", "TagLibAudioTagWriterTests")]
[Trait("Category", "Infrastructure")]
public sealed class TagLibAudioTagWriterTests : BaseTests
{
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
    public void ApplyAsinTag_Mpeg4TagIsACombinedTag_StillReachesTheAppleTag()
    {
        // This is the shape TagLib gives an m4b: File.Tag is a CombinedTag wrapping the Apple
        // tag, so a type test on File.Tag matches nothing and the save writes an unchanged
        // file. Only asking for the tag by type reaches it.
        var appleTag = new TagLib.Mpeg4.AppleTag(new TagLib.Mpeg4.IsoUserDataBox());
        using var file = new StubTagLibFile(
            new TagLib.CombinedTag(appleTag),
            new Dictionary<TagLib.TagTypes, TagLib.Tag?>
            {
                [TagLib.TagTypes.Apple] = appleTag
            });

        TagLibAudioTagWriter.ApplyAsinTag(file, "B0TESTASIN");

        Assert.Equal("B0TESTASIN", appleTag.GetDashBox("com.apple.iTunes", "ASIN"));
        Assert.Contains((TagLib.TagTypes.Apple, true), file.Requests);
    }

    [Fact]
    public void ApplyAsinTag_ContainerHasNoAppleTag_FallsThroughToId3v2()
    {
        // The Apple branch now asks for the tag with create: true, so this pins that a
        // container which does not answer to Apple at all still takes the ID3v2 branch.
        var id3Tag = new TagLib.Id3v2.Tag();
        using var file = new StubTagLibFile(
            id3Tag,
            new Dictionary<TagLib.TagTypes, TagLib.Tag?>
            {
                [TagLib.TagTypes.Id3v2] = id3Tag
            });

        TagLibAudioTagWriter.ApplyAsinTag(file, "B0TESTASIN");

        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, "ASIN", false);
        Assert.NotNull(frame);
        Assert.Equal(["B0TESTASIN"], frame.Text);
    }

    /// <summary>
    /// A TagLib.File that owns no bytes. It records every GetTag request and answers from a
    /// fixed map, which is enough to drive ApplyAsinTag without a parseable container on disk.
    /// </summary>
    private sealed class StubTagLibFile : TagLib.File
    {
        private readonly TagLib.Tag _tag;
        private readonly IReadOnlyDictionary<TagLib.TagTypes, TagLib.Tag?> _tagsByType;

        public StubTagLibFile(
            TagLib.Tag tag,
            IReadOnlyDictionary<TagLib.TagTypes, TagLib.Tag?> tagsByType)
            : base(new EmptyFileAbstraction())
        {
            _tag = tag;
            _tagsByType = tagsByType;
        }

        public List<(TagLib.TagTypes Types, bool Create)> Requests { get; } = [];

        public override TagLib.Tag Tag => _tag;

        public override TagLib.Properties Properties =>
            throw new NotSupportedException("The stub exposes no audio properties.");

        public override void Save() =>
            throw new NotSupportedException("The stub has nothing to save.");

        public override void RemoveTags(TagLib.TagTypes types) =>
            throw new NotSupportedException("The stub removes nothing.");

        public override TagLib.Tag? GetTag(TagLib.TagTypes type, bool create)
        {
            Requests.Add((type, create));
            return _tagsByType.TryGetValue(type, out var tag) ? tag : null;
        }

        private sealed class EmptyFileAbstraction : IFileAbstraction
        {
            public string Name => "stub.m4b";

            public Stream ReadStream => Stream.Null;

            public Stream WriteStream => Stream.Null;

            public void CloseStream(Stream stream)
            {
            }
        }
    }
}
