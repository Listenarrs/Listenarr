using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Downloads;

/// <summary>
/// A file mutation failure reaches here already wrapped, so the outer message names the operation
/// and not the reason. These assert the reason survives into the record an operator actually reads.
/// </summary>
[Trait("Area", "Downloads")]
[Trait("Name", "ImportResultCauseTests")]
[Trait("Category", "Domain")]
public sealed class ImportResultCauseTests : BaseTests
{
    [Fact]
    public void Exception_KeepsTheInnerCause_NotJustTheWrapper()
    {
        // The exact shape FileMover produces: the real reason wrapped in a message that
        // names the operation.
        var cause = new IOException("Invalid cross-device link");
        var wrapped = new InvalidOperationException(
            "Unable to perform HardlinkCopy on /a/source.m4b to /b/dest.m4b", cause);

        var result = ImportResult.Exception(wrapped, "/a/source.m4b");

        Assert.False(result.Success);
        Assert.Contains("Unable to perform HardlinkCopy", result.Message);
        Assert.Contains("Invalid cross-device link", result.Message);
        Assert.Contains("IOException", result.Message);
    }

    [Fact]
    public void Exception_WalksTheWholeChain()
    {
        var root = new UnauthorizedAccessException("Access to the path is denied");
        var middle = new IOException("The process cannot access the file", root);
        var outer = new InvalidOperationException("Unable to perform Move", middle);

        var message = ImportResult.Exception(outer).Message;

        Assert.Contains("Unable to perform Move", message);
        Assert.Contains("The process cannot access the file", message);
        Assert.Contains("Access to the path is denied", message);
    }

    [Fact]
    public void Exception_WithNoInnerCause_IsTypeThenMessage()
    {
        // The control, and a behaviour change worth naming: on canary this row read "Disk full",
        // and it now reads "IOException: Disk full". Every persisted import failure gains the
        // type prefix, not only the wrapped ones, so the equality here is exact on purpose.
        var message = ImportResult.Exception(new IOException("Disk full")).Message;

        Assert.Equal("IOException: Disk full", message);
    }

    [Fact]
    public void Exception_RepeatedMessageInTheChain_IsNotRepeatedInTheRow()
    {
        // The de-duplication is the reason DescribeWithCauses keeps a list rather than appending
        // blindly: a rethrow of the same type and text would otherwise double the row.
        var inner = new IOException("Disk full");
        var outer = new IOException("Disk full", inner);

        Assert.Equal("IOException: Disk full", ImportResult.Exception(outer).Message);
    }
}
