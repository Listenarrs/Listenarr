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
