using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Common;

[Trait("Name", "FileMoveOperationIdentityTests")]
[Trait("Category", "Application")]
public sealed class FileMoveOperationIdentityTests : BaseTests
{
    [Fact]
    public void CreateForPaths_CaseInsensitiveAliasesShareOperationId()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        var first = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            @"C:\Downloads\Author\Book.m4b",
            semantics,
            @"D:\Library\Author\Book.m4b",
            semantics);
        var alias = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            @"c:\downloads\AUTHOR\BOOK.M4B",
            semantics,
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

        var first = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            "/downloads/Author/Book.m4b",
            semantics,
            "/library/Author/Book.m4b",
            semantics);
        var distinct = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            "/downloads/author/Book.m4b",
            semantics,
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

        var copy = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Copy,
            "/downloads/book.m4b",
            semantics,
            "/library/book.m4b",
            semantics);
        var move = FileMoveOperationIdentity.CreateForPaths(
            "download-import",
            42,
            FileAction.Move,
            "/downloads/book.m4b",
            semantics,
            "/library/book.m4b",
            semantics);

        Assert.NotEqual(copy, move);
    }
}
