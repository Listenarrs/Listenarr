/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Tests.Builders;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Name", "ScanFileDiscoveryTests")]
[Trait("Category", "Infrastructure")]
public sealed class ScanFileDiscoveryTests : BaseTests, IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        $"listenarr-scan-discovery-{Guid.NewGuid():N}");

    public ScanFileDiscoveryTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void FindMatchingAudioFiles_SameAuthorSiblingBooks_ReturnsOnlyRequestedBook()
    {
        var requested = CreateAudioFile("Shared Author", "Book One", "Book One.m4b");
        _ = CreateAudioFile("Shared Author", "Book Two", "Book Two.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Book One")
            .WithAuthor("Shared Author")
            .Build();

        var result = Discover(audiobook);

        var found = Assert.Single(result);
        Assert.Equal(requested, found);
    }

    [Fact]
    public void FindMatchingAudioFiles_SameTitleDifferentAuthors_ReturnsOnlyRequestedAuthor()
    {
        var requested = CreateAudioFile("Author One", "Shared Title", "Shared Title.m4b");
        _ = CreateAudioFile("Author Two", "Shared Title", "Shared Title.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Shared Title")
            .WithAuthor("Author One")
            .Build();

        var result = Discover(audiobook);

        var found = Assert.Single(result);
        Assert.Equal(requested, found);
    }

    [Fact]
    public void FindMatchingAudioFiles_ShortTitle_DoesNotMatchSubstringInUnrelatedFilename()
    {
        var requested = CreateAudioFile("Author", "It", "It.m4b");
        _ = CreateAudioFile("Other Author", "Little Women", "Little Women.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("It")
            .WithAuthor("Author")
            .Build();

        var result = Discover(audiobook);

        var found = Assert.Single(result);
        Assert.Equal(requested, found);
    }

    [Fact]
    public void FindMatchingAudioFiles_NestedDiscDirectories_ReturnsAllFilesBelowBookBoundary()
    {
        var first = CreateAudioFile("Author", "Book", "CD1", "01.mp3");
        var second = CreateAudioFile("Author", "Book", "CD2", "02.mp3");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();

        var result = Discover(audiobook);

        Assert.Equal(
            [first, second],
            result.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Discover_StableIdentifierBoundary_RejectsOutsideExactTitleFile()
    {
        var inside = CreateAudioFile(
            "Shared Author",
            "Requested Book B012345678",
            "01.m4b");
        var outside = CreateAudioFile(
            "Shared Author",
            "Unrelated Sibling",
            "Requested Book.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Requested Book")
            .WithAuthor("Shared Author")
            .Build();
        audiobook.Asin = "B012345678";

        var discovery = DiscoverResult(audiobook);

        Assert.Equal(inside, Assert.Single(discovery.AttributedFiles));
        Assert.DoesNotContain(outside, discovery.AttributedFiles);
        Assert.Contains(discovery.Issues, issue =>
            issue.Path == outside
            && issue.Kind == ScanDiscoveryIssueKind.OutsideStableIdentifierBoundary);
    }

    [Fact]
    public void Discover_ConflictingStableIdentifierBoundaries_FailsClosed()
    {
        _ = CreateAudioFile("Author", "Book B012345678", "01.m4b");
        _ = CreateAudioFile("Author", "Alternate B012345678", "02.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();
        audiobook.Asin = "B012345678";

        var discovery = DiscoverResult(audiobook);

        Assert.Empty(discovery.AttributedFiles);
        Assert.Null(discovery.SelectedStableIdentifierBoundary);
        Assert.True(discovery.HasStableIdentifierBoundaryConflict);
        Assert.Contains(discovery.Issues, issue =>
            issue.Kind == ScanDiscoveryIssueKind.AttributionConflict);
    }

    [Fact]
    public void Discover_ExistingOwnedFileOutsideStableIdentifierBoundary_IsPreservedWithoutBoundaryEvidence()
    {
        var inside = CreateAudioFile("Author", "Book B012345678", "01.m4b");
        var outside = CreateAudioFile("Author", "Legacy", "legacy.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();
        audiobook.Asin = "B012345678";

        var discovery = ScanFileDiscovery.Discover(
            new LocalFileSystem(),
            _root,
            audiobook,
            Guid.NewGuid(),
            NullLogger.Instance,
            FileSystemPathSemantics.CurrentHostDefault,
            [outside]);

        Assert.Equal(
            [inside, outside],
            discovery.AttributedFiles.OrderBy(path => path).ToArray());
        Assert.NotNull(discovery.SelectedStableIdentifierBoundary);
        Assert.False(discovery.ProvenBookBoundaries.ContainsKey(outside));
    }

    [Fact]
    public void Discover_DirectoryNamespaceChangesDuringEnumeration_BlocksReconciliation()
    {
        var audioPath = CreateAudioFile("Book.m4b");
        var local = new LocalFileSystem();
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(candidate => candidate.EnumerateFiles(_root))
            .Returns(() => local.EnumerateFiles(_root));
        fileSystem.Setup(candidate => candidate.EnumerateDirectories(_root))
            .Returns(() =>
            {
                var transientPath = Path.Join(_root, "transient-entry.txt");
                File.WriteAllText(transientPath, "transient");
                File.Delete(transientPath);
                return local.EnumerateDirectories(_root);
            });
        fileSystem.Setup(candidate => candidate.IsReparsePoint(It.IsAny<string>()))
            .Returns((string path) => local.IsReparsePoint(path));
        var audiobook = new AudiobookBuilder()
            .WithTitle("Book")
            .Build();

        var discovery = ScanFileDiscovery.Discover(
            fileSystem.Object,
            _root,
            audiobook,
            Guid.NewGuid(),
            NullLogger.Instance,
            FileSystemPathSemantics.CurrentHostDefault);

        Assert.False(discovery.CanReconcile);
        Assert.DoesNotContain(audioPath, discovery.Candidates);
        Assert.Contains(discovery.Issues, issue =>
            issue.Kind == ScanDiscoveryIssueKind.DirectoryGenerationChanged
            && issue.Path == _root);
        fileSystem.VerifyAll();
    }

    [LinuxFact]
    public void Discover_NamedPipeWithAudioExtension_IsSkippedWithoutBlocking()
    {
        var pipePath = Path.Join(_root, "pipe.m4b");
        var startInfo = new System.Diagnostics.ProcessStartInfo("mkfifo")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(pipePath);
        using (var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mkfifo."))
        {
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        var audiobook = new AudiobookBuilder()
            .WithTitle("pipe")
            .Build();

        var discovery = ScanFileDiscovery.Discover(
            new LocalFileSystem(),
            _root,
            audiobook,
            Guid.NewGuid(),
            NullLogger.Instance,
            FileSystemPathSemantics.CurrentHostDefault,
            requireDurableGenerationProof: false);

        Assert.DoesNotContain(pipePath, discovery.Candidates);
        Assert.Contains(discovery.Issues, issue =>
            issue.Kind == ScanDiscoveryIssueKind.LinkSkipped
            && issue.Path == pipePath
            && issue.Message.Contains("Non-regular", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_EnumerationFailure_DoesNotExposeExceptionMessage()
    {
        const string secret = "secret-volume-path";
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(candidate => candidate.EnumerateFiles(_root))
            .Throws(new IOException(secret));
        var audiobook = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();

        var discovery = ScanFileDiscovery.Discover(
            fileSystem.Object,
            _root,
            audiobook,
            Guid.NewGuid(),
            NullLogger.Instance,
            FileSystemPathSemantics.CurrentHostDefault);

        var issue = Assert.Single(discovery.Issues);
        Assert.Equal(ScanDiscoveryIssueKind.EnumerationFailure, issue.Kind);
        Assert.Equal("The path could not be enumerated safely.", issue.Message);
        Assert.DoesNotContain(secret, issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FileSystemCaseSensitivity.Sensitive, false)]
    [InlineData(FileSystemCaseSensitivity.Insensitive, true)]
    public void CanClaimNewPath_UsesPersistedFilesystemCaseSemantics(
        FileSystemCaseSensitivity caseSensitivity,
        bool expected)
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            caseSensitivity);

        var result = ScanFileDiscovery.CanClaimNewPath(
            "/library/book/file.m4b",
            "/Library/Book",
            new HashSet<string>(semantics.Comparer),
            semantics);

        Assert.Equal(expected, result);
    }

    [DirectoryLinkFact]
    public void Discover_LinkedDirectoryInsideIdentifierBoundary_IsNotTraversed()
    {

        var identifierDirectory = Path.Join(_root, "Author", "Book B012345678");
        var foreignDirectory = Path.Join(_root, "foreign");
        Directory.CreateDirectory(identifierDirectory);
        Directory.CreateDirectory(foreignDirectory);
        var foreign = Path.Join(foreignDirectory, "foreign.m4b");
        File.WriteAllText(foreign, "audio");
        File.WriteAllText(Path.Join(identifierDirectory, "01.m4b"), "audio");
        var link = Path.Join(identifierDirectory, "linked-disc");
        Directory.CreateSymbolicLink(link, foreignDirectory);
        var audiobook = new AudiobookBuilder()
            .WithTitle("Book")
            .WithAuthor("Author")
            .Build();
        audiobook.Asin = "B012345678";

        var discovery = DiscoverResult(audiobook);

        Assert.Equal(identifierDirectory, discovery.SelectedStableIdentifierBoundary);
        Assert.Contains(foreign, discovery.Candidates);
        Assert.DoesNotContain(Path.Join(link, "foreign.m4b"), discovery.Candidates);
        Assert.Contains(discovery.Issues, issue =>
            issue.Kind == ScanDiscoveryIssueKind.LinkSkipped
            && issue.Path == link);
    }

    [Fact]
    public void FindMatchingAudioFiles_AuthorHasPostNominalInMetadata_MatchesCleanFolder()
    {
        var requested = CreateAudioFile("Jane Doe", "Study", "Study.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Study")
            .WithAuthor("Jane Doe, PhD")
            .Build();

        var result = Discover(audiobook);

        Assert.Equal(requested, Assert.Single(result));
    }

    [Fact]
    public void FindMatchingAudioFiles_AuthorFolderHasGenerationalSuffix_MatchesCleanMetadata()
    {
        var requested = CreateAudioFile("Martin King Jr", "Speeches", "Speeches.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Speeches")
            .WithAuthor("Martin King")
            .Build();

        var result = Discover(audiobook);

        Assert.Equal(requested, Assert.Single(result));
    }

    [Fact]
    public void FindMatchingAudioFiles_AmbiguousShortWordSuffix_IsNotStrippedAsPostNominal()
    {
        // "Ma" is a real surname (e.g. Yo-Yo Ma), not a post-nominal, so a "Yo Yo" folder must
        // not capture a "Yo Yo Ma" book. Excluding short ambiguous degree abbreviations keeps
        // the normalization from silently dropping surnames.
        _ = CreateAudioFile("Yo Yo", "Suite", "Suite.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Suite")
            .WithAuthor("Yo Yo Ma")
            .Build();

        var result = Discover(audiobook);

        Assert.Empty(result);
    }

    [Fact]
    public void FindMatchingAudioFiles_AuthorInitials_AreNotExpandedToFullName()
    {
        // Post-nominals only: initials such as "L. M." must not be treated as equivalent to a
        // spelled-out given name, which would over-match otherwise-distinct authors.
        _ = CreateAudioFile("Lucy Maud Montgomery", "Avonlea", "Avonlea.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Avonlea")
            .WithAuthor("L. M. Montgomery")
            .Build();

        var result = Discover(audiobook);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Jane Doe, Ph.D.")]
    [InlineData("Jane Doe, M.D.")]
    [InlineData("Jane Doe, J.D.")]
    [InlineData("Jane Doe, Ed.D.")]
    [InlineData("Jane Doe, D.D.S.")]
    [InlineData("Jane Doe, LL.M.")]
    [InlineData("Jane Doe, M.D., Ph.D.")]
    public void FindMatchingAudioFiles_AuthorHasDottedPostNominalInMetadata_MatchesCleanFolder(
        string author)
    {
        // Dotted spellings ("Ph.D.") must match a clean folder just like the plain form
        // ("PhD") already does, including stacked credentials.
        var requested = CreateAudioFile("Jane Doe", "Study", "Study.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Study")
            .WithAuthor(author)
            .Build();

        var result = Discover(audiobook);

        Assert.Equal(requested, Assert.Single(result));
    }

    [Fact]
    public void FindMatchingAudioFiles_AuthorFolderHasDottedPostNominal_MatchesCleanMetadata()
    {
        // The reverse direction: a folder carrying a dotted credential matches clean metadata.
        // No trailing dot — Windows strips those from directory names, which is also exactly how
        // such a folder ends up on disk ("Martin King Ph.D.") -> "Martin King Ph.D").
        var requested = CreateAudioFile("Martin King Ph.D", "Speeches", "Speeches.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Speeches")
            .WithAuthor("Martin King")
            .Build();

        var result = Discover(audiobook);

        Assert.Equal(requested, Assert.Single(result));
    }

    [Theory]
    [InlineData("Jane Doe, B.S.")]
    [InlineData("Jane Doe, A.B.")]
    public void FindMatchingAudioFiles_UncuratedDottedAbbreviation_IsNotStripped(string author)
    {
        // Short abbreviations outside the curated list (e.g. "B.S.", "A.B.") must not be
        // stripped, so the full name stays distinct and a bare "Jane Doe" folder is not captured.
        _ = CreateAudioFile("Jane Doe", "Study", "Study.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Study")
            .WithAuthor(author)
            .Build();

        Assert.Empty(Discover(audiobook));
    }

    [Fact]
    public void FindMatchingAudioFiles_TrailingDottedInitials_AreNotStrippedAsPostNominal()
    {
        // Trailing initials ("L.M.") are not a credential and must not be collapsed/stripped,
        // which would otherwise over-match a bare surname folder.
        _ = CreateAudioFile("Montgomery", "Avonlea", "Avonlea.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Avonlea")
            .WithAuthor("Montgomery L.M.")
            .Build();

        Assert.Empty(Discover(audiobook));
    }

    [Theory]
    [InlineData("Jane Doe, Ph.D., CPA")]
    [InlineData("Jane Doe, CPA, Ph.D.")]
    public void FindMatchingAudioFiles_MixedCredentialStack_MatchesCleanFolder(string author)
    {
        // A stack mixing a dotted and a plain credential, in either order, must still reduce to
        // the clean name so a bare author folder matches. The walk crosses the plain credential
        // to reach the dotted one instead of stopping at it (#990 review).
        var requested = CreateAudioFile("Jane Doe", "Study", "Study.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Study")
            .WithAuthor(author)
            .Build();

        Assert.Equal(requested, Assert.Single(Discover(audiobook)));
    }

    [Fact]
    public void FindMatchingAudioFiles_SpacedCredentialFolder_MatchesDottedRecord()
    {
        // Regression: a "Jane Doe Ph D" folder must still match a "Jane Doe, Ph.D." record.
        // Preserving the original author token restores this; the collapsed/stripped aliases
        // alone missed the spaced spelling (#990 review).
        var requested = CreateAudioFile("Jane Doe Ph D", "Study", "Study.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Study")
            .WithAuthor("Jane Doe, Ph.D.")
            .Build();

        Assert.Equal(requested, Assert.Single(Discover(audiobook)));
    }

    [Fact]
    public void FindMatchingAudioFiles_DottedCredentialFolder_MatchesSpacedRecord()
    {
        // The reverse direction: a dotted-credential folder must match a spaced-credential record.
        // Keeping the folder's original normalization (before collapsing) restores it (#990 review).
        var requested = CreateAudioFile("Jane Doe Ph.D", "Study", "Study.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Study")
            .WithAuthor("Jane Doe Ph D")
            .Build();

        Assert.Equal(requested, Assert.Single(Discover(audiobook)));
    }

    [Fact]
    public void FindMatchingAudioFiles_CredentialLikeTokenWithExtraCharacters_IsNotStripped()
    {
        // "Ph.D.2" is not a credential. Stripping only the periods keeps it as "phd2" (not a
        // curated post-nominal), so a bare "Jane Doe" folder is not wrongly captured (#990 review).
        _ = CreateAudioFile("Jane Doe", "Study", "Study.m4b");
        var audiobook = new AudiobookBuilder()
            .WithTitle("Study")
            .WithAuthor("Jane Doe, Ph.D.2")
            .Build();

        Assert.Empty(Discover(audiobook));
    }

    private List<string> Discover(Audiobook audiobook) =>
        DiscoverResult(audiobook).AttributedFiles.ToList();

    private ScanDiscoveryResult DiscoverResult(Audiobook audiobook) =>
        ScanFileDiscovery.Discover(
            new LocalFileSystem(),
            _root,
            audiobook,
            Guid.NewGuid(),
            NullLogger.Instance,
            FileSystemPathSemantics.CurrentHostDefault);

    private string CreateAudioFile(params string[] segments)
    {
        var path = Path.Join([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "audio");
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temporary test data.
        }
    }
}
