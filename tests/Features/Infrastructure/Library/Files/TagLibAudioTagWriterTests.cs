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
}
