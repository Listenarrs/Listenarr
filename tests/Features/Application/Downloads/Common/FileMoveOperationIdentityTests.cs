using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Common;

[Trait("Name", "FileMoveOperationIdentityTests")]
[Trait("Category", "Application")]
public sealed class FileMoveOperationIdentityTests : BaseTests
{
    private static FilePublicationSourceProof Proof(
        string physicalObjectIdentity,
        long length = 5,
        char hashDigit = 'A') =>
        new(physicalObjectIdentity, length, new string(hashDigit, 64));

    [Fact]
    public void CreateForPaths_CaseInsensitiveAliasesShareOperationId()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);
        var proof = Proof("source-generation-1");

        var first = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            @"C:\Downloads\Author\Book.m4b",
            semantics,
            proof,
            @"D:\Library\Author\Book.m4b",
            semantics);
        var alias = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            @"c:\downloads\AUTHOR\BOOK.M4B",
            semantics,
            proof,
            @"d:\library\author\book.M4B",
            semantics);

        Assert.Equal(first, alias);
    }

    [Fact]
    public void CreateForPaths_CaseSensitivePathsRemainDistinct()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var proof = Proof("source-generation-1");

        var first = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            "/downloads/Author/Book.m4b",
            semantics,
            proof,
            "/library/Author/Book.m4b",
            semantics);
        var distinct = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            "/downloads/author/Book.m4b",
            semantics,
            proof,
            "/library/Author/Book.m4b",
            semantics);

        Assert.NotEqual(first, distinct);
    }

    [Fact]
    public void CreateForPaths_OperationKindRemainsPartOfIdentity()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var proof = Proof("source-generation-1");

        var copy = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Copy,
            "/downloads/book.m4b",
            semantics,
            proof,
            "/library/book.m4b",
            semantics);
        var move = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            "/downloads/book.m4b",
            semantics,
            proof,
            "/library/book.m4b",
            semantics);

        Assert.NotEqual(copy, move);
    }

    [Fact]
    public void CreateForPaths_SourcePhysicalGenerationRemainsPartOfIdentity()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        var first = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Copy,
            "/downloads/book.m4b",
            semantics,
            Proof("source-generation-1"),
            "/library/book.m4b",
            semantics);
        var replacement = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Copy,
            "/downloads/book.m4b",
            semantics,
            Proof("source-generation-2"),
            "/library/book.m4b",
            semantics);

        Assert.NotEqual(first, replacement);
    }

    [Fact]
    public void CreateForPaths_SourceContentProofRemainsPartOfIdentity()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        var first = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Copy,
            "/downloads/book.m4b",
            semantics,
            Proof("source-generation-1", hashDigit: 'A'),
            "/library/book.m4b",
            semantics);
        var rewritten = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Copy,
            "/downloads/book.m4b",
            semantics,
            Proof("source-generation-1", hashDigit: 'B'),
            "/library/book.m4b",
            semantics);

        Assert.NotEqual(first, rewritten);
    }
}
