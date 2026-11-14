using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "DurableBarrierErrorClassificationTests")]
[Trait("Category", "Infrastructure")]
public sealed class DurableBarrierErrorClassificationTests : BaseTests
{
    [Theory]
    [InlineData(1)]   // ERROR_INVALID_FUNCTION  — SMB / network-mapped drives
    [InlineData(50)]  // ERROR_NOT_SUPPORTED     — some SMB configurations
    public void UnsupportedDurableBarrierError_DegradesForFilesystemsThatCannotFlush(int win32Error)
    {
        Assert.True(PinnedDirectoryCreation.IsUnsupportedDurableBarrierError(win32Error));
    }

    [Theory]
    [InlineData(0)]    // success
    [InlineData(2)]    // ERROR_FILE_NOT_FOUND
    [InlineData(5)]    // ERROR_ACCESS_DENIED
    [InlineData(21)]   // ERROR_NOT_READY
    [InlineData(1117)] // ERROR_IO_DEVICE
    public void RealFailures_StillThrow(int win32Error)
    {
        Assert.False(PinnedDirectoryCreation.IsUnsupportedDurableBarrierError(win32Error));
    }
}
