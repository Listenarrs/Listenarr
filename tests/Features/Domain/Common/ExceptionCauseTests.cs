using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Common;

/// <summary>
/// Two sites had grown their own copy of "why did this fail", and they disagreed: one walked the
/// inner chain and one formatted the outer exception alone, so the same wrapped failure read
/// differently depending on which record you looked at. These pin the one spelling both now use.
/// </summary>
[Trait("Area", "Common")]
[Trait("Name", "ExceptionCauseTests")]
[Trait("Category", "Domain")]
public sealed class ExceptionCauseTests : BaseTests
{
    [Fact]
    public void Describe_SingleException_IsTypeThenMessage()
    {
        Assert.Equal("IOException: Disk full", ExceptionCause.Describe(new IOException("Disk full")));
    }

    [Fact]
    public void Describe_WalksTheWholeChain()
    {
        // The exact shape the file layer produces: the real reason wrapped in a message that
        // names the operation.
        var root = new UnauthorizedAccessException("Access to the path is denied");
        var middle = new IOException("Invalid cross-device link", root);
        var outer = new InvalidOperationException("Unable to perform HardlinkCopy", middle);

        Assert.Equal(
            "InvalidOperationException: Unable to perform HardlinkCopy -> "
            + "IOException: Invalid cross-device link -> "
            + "UnauthorizedAccessException: Access to the path is denied",
            ExceptionCause.Describe(outer));
    }

    [Fact]
    public void Describe_RepeatedFrame_IsNotRepeated()
    {
        // A rethrow of the same type and text would otherwise say nothing twice.
        var inner = new IOException("Disk full");

        Assert.Equal("IOException: Disk full", ExceptionCause.Describe(new IOException("Disk full", inner)));
    }

    [Fact]
    public void Describe_StopsAtTheDepthGuard()
    {
        // The guard is why this walks rather than recurses: an exception can be constructed with
        // itself somewhere in its own chain, and the frames here are distinct so nothing is
        // collapsed on the way. Stopping at two proves the parameter is honoured rather than
        // hardcoded, which a private copy in either call site would have been.
        Exception current = new IOException("frame 0");
        for (var depth = 1; depth <= 4; depth++)
        {
            current = new IOException($"frame {depth}", current);
        }

        Assert.Equal("IOException: frame 4 -> IOException: frame 3", ExceptionCause.Describe(current, maxDepth: 2));
        Assert.Equal(5, ExceptionCause.Describe(current).Split(" -> ").Length);
    }

    [Fact]
    public void Describe_Null_IsEmpty()
    {
        // The call sites take a non-nullable exception, so this is a guard rather than a path.
        // It is here because the behaviour it replaced was a NullReferenceException.
        Assert.Equal(string.Empty, ExceptionCause.Describe(null));
    }
}
