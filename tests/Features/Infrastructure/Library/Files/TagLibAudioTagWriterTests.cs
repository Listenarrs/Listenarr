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
        lease.SetupGet(candidate => candidate.SupportsMetadataWrite)
            .Returns(false);
        lease.SetupGet(candidate => candidate.PublicPath)
            .Returns("scan-only-book.m4b");
        var writer = new TagLibAudioTagWriter(
            Mock.Of<ILogger<TagLibAudioTagWriter>>());

        await writer.WriteAsinTagAsync(lease.Object, "B0TESTASIN");

        lease.VerifyGet(candidate => candidate.SupportsMetadataWrite, Times.Once);
        lease.VerifyGet(candidate => candidate.PublicPath, Times.Once);
        lease.VerifyNoOtherCalls();
    }
}
