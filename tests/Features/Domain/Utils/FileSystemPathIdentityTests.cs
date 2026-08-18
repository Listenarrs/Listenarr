using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Utils;

[Trait("Name", "FileSystemPathIdentityTests")]
[Trait("Category", "Domain")]
public sealed class FileSystemPathIdentityTests : BaseTests
{
    [Theory]
    [InlineData(nameof(FileSystemPathSyntax.Unix))]
    [InlineData(nameof(FileSystemPathSyntax.Windows))]
    public void UnambiguousStoredAbsolutePath_DoubleForwardSlashIsRejected(
        string hostSyntaxName)
    {
        var accepted = FileSystemPathIdentity
            .TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                "//server/share/Book",
                out var canonicalPath,
                out var reason,
                Enum.Parse<FileSystemPathSyntax>(hostSyntaxName));

        Assert.False(accepted);
        Assert.Empty(canonicalPath);
        Assert.Contains("unambiguous", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/library/Book", nameof(FileSystemPathSyntax.Unix), "/library/Book")]
    [InlineData(@"C:\Library\Book", nameof(FileSystemPathSyntax.Windows), @"C:\Library\Book")]
    [InlineData(@"\\server\share\Book", nameof(FileSystemPathSyntax.Windows), @"\\server\share\Book")]
    public void UnambiguousStoredAbsolutePath_ExplicitSyntaxIsAccepted(
        string path,
        string hostSyntaxName,
        string expected)
    {
        var accepted = FileSystemPathIdentity
            .TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out var reason,
                Enum.Parse<FileSystemPathSyntax>(hostSyntaxName));

        Assert.True(accepted, reason);
        Assert.Equal(expected, canonicalPath);
    }

    [Theory]
    [InlineData("//library/books", "/library/books/Author/Title", FileSystemCaseSensitivityMode.Insensitive, true)]
    [InlineData("//library/books", "/other/books/Author/Title", FileSystemCaseSensitivityMode.Insensitive, false)]
    [InlineData("C:\\Library\\Books", "/library/books/Author/Title", FileSystemCaseSensitivityMode.Insensitive, false)]
    public void AmbiguousStoredBoundaryMayContainPath_UsesContextOnlyAsConservativeSafetyFence(
        string storedBoundary,
        string candidatePath,
        FileSystemCaseSensitivityMode mode,
        bool expected)
    {
        var result = FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
            storedBoundary,
            candidatePath,
            FileSystemPathSyntax.Unix,
            mode);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void AmbiguousStoredBoundaryMayContainPath_InvalidSameSyntaxBoundaryFailsClosed()
    {
        Assert.True(FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
            "/library/../books",
            "/library/books/Author/Title",
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivityMode.Sensitive));
    }

    [Fact]
    public void UnixIdentity_PreservesLiteralBackslash()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        Assert.False(FileSystemPathIdentity.AreEquivalent(
            "/books/Author\\Title",
            "/books/Author/Title",
            semantics));
    }

    [Fact]
    public void UnixRoot_ContainsAbsoluteChild()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        Assert.True(FileSystemPathIdentity.IsSameOrInside("/Author/Title", "/", semantics));
    }

    [Fact]
    public void InsensitiveUnixVolume_UsesFilesystemSemantics()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive);

        Assert.True(FileSystemPathIdentity.AreEquivalent(
            "/Volumes/Books/Title",
            "/volumes/books/title/",
            semantics));
    }

    [Fact]
    public void WindowsIdentity_NormalizesSeparatorsAndCase()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.True(FileSystemPathIdentity.AreEquivalent(
            @"C:\Books\Author\Title",
            "c:/books/author/title/",
            semantics));
    }

    [Theory]
    [InlineData(@"\\server\\share\Author\Book", @"\\server\share\Author\Book")]
    [InlineData("//server//share/Author/Book", @"\\server\share\Author\Book")]
    [InlineData(@"\\\server\share\Author\Book", @"\\server\share\Author\Book")]
    public void WindowsIdentity_RepeatedUncSeparatorsDoNotCorruptShareOrRemainder(
        string path,
        string expected)
    {
        Assert.Equal(
            expected,
            FileSystemPathIdentity.Canonicalize(
                path,
                FileSystemPathSyntax.Windows));
    }

    [Theory]
    [InlineData(@"\\server\..\Book")]
    [InlineData(@"\\.\share\Book")]
    public void WindowsIdentity_RejectsNavigationComponentsAsUncAuthority(
        string path)
    {
        Assert.Throws<ArgumentException>(() =>
            FileSystemPathIdentity.Canonicalize(
                path,
                FileSystemPathSyntax.Windows));
    }

    [Fact]
    public void ResolveRelativePath_UnixBackslashRemainsInFilename()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        var resolved = FileSystemPathIdentity.TryResolveRelativePathWithinBase(
            "/target",
            "Author\\Title/book.m4b",
            semantics,
            out var path);

        Assert.True(resolved);
        Assert.Equal("/target/Author\\Title/book.m4b", path);
    }

    [Fact]
    public void GetRelativePath_UsesResolvedCaseSemantics()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive);

        var resolved = FileSystemPathIdentity.TryGetRelativePathWithinBase(
            "/books",
            "/Books/Author/Title",
            semantics,
            out var relativePath);

        Assert.True(resolved);
        Assert.Equal("Author/Title", relativePath);
    }

    [Theory]
    [InlineData(@"Author\Title\book.m4b", FileSystemPathSyntax.Windows, FileSystemPathSyntax.Unix, "Author/Title/book.m4b")]
    [InlineData("Author/Title/book.m4b", FileSystemPathSyntax.Unix, FileSystemPathSyntax.Windows, @"Author\Title\book.m4b")]
    public void ConvertRelativePathSyntax_UsesTargetSeparators(
        string relativePath,
        FileSystemPathSyntax sourceSyntax,
        FileSystemPathSyntax targetSyntax,
        string expected)
    {
        Assert.Equal(
            expected,
            FileSystemPathIdentity.ConvertRelativePathSyntax(relativePath, sourceSyntax, targetSyntax));
    }

    [Fact]
    public void IdentityKey_IsVersionedAndStableForEquivalentPaths()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        var first = FileSystemPathIdentity.CreateKey(
            "move:7",
            @"C:\Books\Title",
            semantics);
        var second = FileSystemPathIdentity.CreateKey(
            "move:7",
            "c:/books/title/",
            semantics);

        Assert.StartsWith("v1:move:7:i:", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    [Fact]
    public void EquivalentEndpoints_EitherInsensitiveIdentityMakesCaseOnlyVariantEquivalent()
    {
        var source = "/library/Book";
        var target = "/library/book/";
        var sourceIdentity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            "/library");
        var targetIdentity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            "/library");

        Assert.True(FileSystemPathIdentity.AreEquivalentEndpoints(
            source,
            sourceIdentity,
            target,
            targetIdentity));
    }

    [Fact]
    public void EquivalentEndpoints_SemanticsOverloadUsesBothEndpointRules()
    {
        var sourceSemantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var targetSemantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive);

        Assert.True(FileSystemPathIdentity.AreEquivalentEndpoints(
            "/library/Book",
            sourceSemantics,
            "/library/book/",
            targetSemantics));
    }

    [Fact]
    public void EquivalentEndpoints_SemanticsOverloadRejectsUnknownSensitivity()
    {
        var known = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var unknown = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Unknown);

        Assert.Throws<InvalidOperationException>(() =>
            FileSystemPathIdentity.AreEquivalentEndpoints(
                "/library/Book",
                known,
                "/library/Book",
                unknown));
    }

    [Fact]
    public void EquivalentEndpoints_BothSensitiveIdentitiesPreserveCaseOnlyDifference()
    {
        var source = "/library/Book";
        var target = "/library/book";
        var identity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            "/library");

        Assert.False(FileSystemPathIdentity.AreEquivalentEndpoints(
            source,
            identity,
            target,
            identity));
    }

    [Theory]
    [InlineData(
        "/Library",
        nameof(FileSystemCaseSensitivity.Sensitive),
        "/library",
        nameof(FileSystemCaseSensitivity.Insensitive),
        nameof(FileSystemPathBoundaryConflict.Equivalent))]
    [InlineData(
        "/Library/Author",
        nameof(FileSystemCaseSensitivity.Sensitive),
        "/library",
        nameof(FileSystemCaseSensitivity.Insensitive),
        nameof(FileSystemPathBoundaryConflict.FirstInsideSecond))]
    [InlineData(
        "/Library",
        nameof(FileSystemCaseSensitivity.Insensitive),
        "/library/Author",
        nameof(FileSystemCaseSensitivity.Sensitive),
        nameof(FileSystemPathBoundaryConflict.SecondInsideFirst))]
    [InlineData(
        "/Library",
        nameof(FileSystemCaseSensitivity.Sensitive),
        "/library",
        nameof(FileSystemCaseSensitivity.Sensitive),
        nameof(FileSystemPathBoundaryConflict.None))]
    public void BoundaryConflict_UsesBothEndpointSemantics(
        string first,
        string firstSensitivityName,
        string second,
        string secondSensitivityName,
        string expectedName)
    {
        var firstSemantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            Enum.Parse<FileSystemCaseSensitivity>(firstSensitivityName));
        var secondSemantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            Enum.Parse<FileSystemCaseSensitivity>(secondSensitivityName));

        var conflict = FileSystemPathIdentity.EvaluateBoundaryConflict(
            first,
            firstSemantics,
            second,
            secondSemantics);

        Assert.Equal(Enum.Parse<FileSystemPathBoundaryConflict>(expectedName), conflict);
    }

    [Fact]
    public void BoundaryConflict_DifferentSyntaxesDoNotOverlap()
    {
        var windows = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);
        var unix = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive);

        Assert.Equal(
            FileSystemPathBoundaryConflict.None,
            FileSystemPathIdentity.EvaluateBoundaryConflict(
                @"C:\Library",
                windows,
                "/Library",
                unix));
    }

    [Fact]
    public void BoundaryConflict_UnknownSensitivityIsRejected()
    {
        var known = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var unknown = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Unknown);

        Assert.Throws<InvalidOperationException>(() =>
            FileSystemPathIdentity.EvaluateBoundaryConflict(
                "/library",
                known,
                "/library/author",
                unknown));
    }

    [Fact]
    public void EquivalentEndpoints_DifferentSyntaxesAreDistinct()
    {
        var sourceIdentity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            @"C:\Library");
        var targetIdentity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            "/c/Library");

        Assert.False(FileSystemPathIdentity.AreEquivalentEndpoints(
            @"C:\Library\Book",
            sourceIdentity,
            "/c/Library/Book",
            targetIdentity));
    }

    [Theory]
    [InlineData(@"C:\Downloads\Author\Book", FileSystemPathSyntax.Unix)]
    [InlineData("/downloads/Author/Book", FileSystemPathSyntax.Windows)]
    public void StoredAbsolutePath_ForeignSyntax_IsPreservedAndRejected(
        string path,
        FileSystemPathSyntax hostSyntax)
    {
        var resolved = FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
            path,
            out var canonicalPath,
            out var reason,
            hostSyntax);

        Assert.False(resolved);
        Assert.Empty(canonicalPath);
        Assert.Contains("filesystem syntax", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Downloads/Author/Book", FileSystemPathSyntax.Unix)]
    [InlineData(@"Downloads\Author\Book", FileSystemPathSyntax.Windows)]
    public void StoredAbsolutePath_RelativeSyntax_IsRejectedWithoutCurrentDirectoryExpansion(
        string path,
        FileSystemPathSyntax hostSyntax)
    {
        var resolved = FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
            path,
            out var canonicalPath,
            out var reason,
            hostSyntax);

        Assert.False(resolved);
        Assert.Empty(canonicalPath);
        Assert.Contains("not absolute", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.CurrentDirectory, canonicalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoredPathWithIdentity_ForeignIdentity_IsRejectedWithoutCanonicalization()
    {
        var identity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            @"C:\Library");

        var resolved = FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
            @"C:\Library\Book",
            identity,
            out var canonicalPath,
            out var reason,
            FileSystemPathSyntax.Unix);

        Assert.False(resolved);
        Assert.Empty(canonicalPath);
        Assert.Contains("persisted identity", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoredPathWithIdentity_PathSyntaxMismatch_IsRejected()
    {
        var identity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            @"C:\Library");

        var resolved = FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
            "/library/Book",
            identity,
            out var canonicalPath,
            out var reason,
            FileSystemPathSyntax.Windows);

        Assert.False(resolved);
        Assert.Empty(canonicalPath);
        Assert.Contains("path uses Unix", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoubleSlashAbsolutePath_RequiresFilesystemContext()
    {
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            "//server/share/Books/Title",
            out _));
    }

    [Fact]
    public void ForwardSlashUnc_IsDetectedAndCanonicalizedAsWindows()
    {
        Assert.True(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            "//server/share/Books/Title",
            FileSystemPathSyntax.Windows,
            out var syntax));
        Assert.Equal(FileSystemPathSyntax.Windows, syntax);

        var resolved = FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
            "//server/share/Books/Title",
            out var canonicalPath,
            out var reason,
            FileSystemPathSyntax.Windows);

        Assert.True(resolved, reason);
        Assert.Equal(@"\\server\share\Books\Title", canonicalPath);
    }

    [Fact]
    public void DoubleSlashAbsolutePath_IsUnixWhenHostIsUnix()
    {
        Assert.True(FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
            "//tmp/library/Title",
            out var syntax,
            FileSystemPathSyntax.Unix));
        Assert.Equal(FileSystemPathSyntax.Unix, syntax);

        var resolved = FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
            "//tmp/library/Title",
            out var canonicalPath,
            out var reason,
            FileSystemPathSyntax.Unix);

        Assert.True(resolved, reason);
        Assert.Equal("/tmp/library/Title", canonicalPath);
    }

    [Theory]
    [InlineData("/library/./Title", FileSystemPathSyntax.Unix)]
    [InlineData("/library/../Title", FileSystemPathSyntax.Unix)]
    [InlineData(@"C:\Library\.\Title", FileSystemPathSyntax.Windows)]
    [InlineData(@"C:\Library\..\Title", FileSystemPathSyntax.Windows)]
    [InlineData(@"C:/Library\../Title", FileSystemPathSyntax.Windows)]
    public void StoredAbsolutePath_NavigationSegment_IsRejectedWithoutCanonicalization(
        string path,
        FileSystemPathSyntax hostSyntax)
    {
        var resolved = FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
            path,
            out var canonicalPath,
            out var reason,
            hostSyntax);

        Assert.False(resolved);
        Assert.Empty(canonicalPath);
        Assert.Contains("navigation segment", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoredPathWithIdentity_NavigationBearingBoundary_IsRejected()
    {
        var identity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            "/library/../library");

        var resolved = FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
            "/library/Title",
            identity,
            out var canonicalPath,
            out var reason,
            FileSystemPathSyntax.Unix);

        Assert.False(resolved);
        Assert.Empty(canonicalPath);
        Assert.Contains("identity boundary", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoredBoundaryMayContainPath_WindowsNamespaceAlias_ContainsOrdinaryChild()
    {
        Assert.True(FileSystemPathIdentity.StoredBoundaryMayContainPath(
            @"\\?\C:\Library",
            @"C:\Library\Author\Book",
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivityMode.Insensitive));
    }

    [Fact]
    public void StoredBoundaryMayContainPath_WindowsNamespaceDifferentDrive_DoesNotContainCandidate()
    {
        Assert.False(FileSystemPathIdentity.StoredBoundaryMayContainPath(
            @"\\?\D:\Other",
            @"C:\Library\Author\Book",
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivityMode.Insensitive));
    }

    [Fact]
    public void StoredPathMayIdentifySamePath_WindowsNamespaceAlias_MatchesOrdinaryPath()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.True(FileSystemPathIdentity.StoredPathMayIdentifySamePath(
            @"\\?\C:\Library\Author\Book",
            @"C:\Library\Author\Book",
            semantics));
    }

    [Fact]
    public void StoredPathMayIdentifySamePath_WindowsNamespaceDifferentDrive_IsDistinct()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.False(FileSystemPathIdentity.StoredPathMayIdentifySamePath(
            @"\\?\D:\Other\Book",
            @"C:\Library\Author\Book",
            semantics));
    }

    [Fact]
    public void StoredPathMayIdentifySamePath_NestedPath_IsNotExactDuplicate()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.False(FileSystemPathIdentity.StoredPathMayIdentifySamePath(
            @"C:\Library\Author\Book\Disc 1",
            @"C:\Library\Author\Book",
            semantics));
    }

    [Fact]
    public void StoredPathMayTouchBoundary_WindowsNamespacePath_FailsClosed()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.True(FileSystemPathIdentity.StoredPathMayTouchBoundary(
            @"\\?\C:\Library\Author\Book",
            @"C:\Library",
            semantics));
    }

    [Fact]
    public void StoredPathMayTouchBoundary_NamespacePathWithIncompatiblePersistedIdentity_FailsClosed()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);
        var persistedIdentity = new PathIdentitySnapshot(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            @"C:\Library");

        Assert.True(FileSystemPathIdentity.StoredPathMayTouchBoundary(
            @"\\?\D:\Other\Book",
            @"C:\Library",
            semantics,
            persistedIdentity));
    }

    [Fact]
    public void StoredPathMayTouchBoundary_WindowsNamespacePathOnDifferentDrive_IsOutside()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.False(FileSystemPathIdentity.StoredPathMayTouchBoundary(
            @"\\?\D:\Other\Book",
            @"C:\Library",
            semantics));
    }

    [Fact]
    public void UnambiguousStoredPath_WindowsNamespacePath_IsRejected()
    {
        var resolved = FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
            @"\\?\C:\Library\Book",
            out var canonicalPath,
            out var reason,
            FileSystemPathSyntax.Windows);

        Assert.False(resolved);
        Assert.Empty(canonicalPath);
        Assert.Contains("namespace path", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoredPathMayTouchBoundary_AmbiguousSameHostPath_FailsClosed()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.True(FileSystemPathIdentity.StoredPathMayTouchBoundary(
            "//server/share/Book",
            @"C:\Library",
            semantics));
    }

    [Fact]
    public void StoredPathMayTouchBoundary_ClearlyForeignSyntax_IsOutside()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.False(FileSystemPathIdentity.StoredPathMayTouchBoundary(
            "/foreign/library/Book",
            @"C:\Library",
            semantics));
    }

    [Fact]
    public void StoredPathMayTouchBoundary_UnrelatedSameSyntaxPath_IsOutside()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);

        Assert.False(FileSystemPathIdentity.StoredPathMayTouchBoundary(
            @"D:\Other\Book",
            @"C:\Library",
            semantics));
    }

    [Fact]
    public void UnknownSensitivity_CannotCreateIdentityKey()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Unknown);

        Assert.Throws<InvalidOperationException>(() =>
            FileSystemPathIdentity.CreateKey("root", "/books", semantics));
    }
}
