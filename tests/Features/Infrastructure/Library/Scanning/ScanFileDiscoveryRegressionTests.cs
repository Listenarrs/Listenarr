/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 */
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Name", "ScanFileDiscoveryRegressionTests")]
[Trait("Category", "Infrastructure")]
public sealed class ScanFileDiscoveryRegressionTests : BaseTests, IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        $"listenarr-scan-regression-{Guid.NewGuid():N}");

    public ScanFileDiscoveryRegressionTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Discover_ScanningOneBook_DoesNotClaimOtherBooksBySameAuthor()
    {
        var expected = CreateAudioFile(
            "H. Rider Haggard",
            "H. Rider Haggard - She",
            "She.m4b");
        _ = CreateAudioFile(
            "H. Rider Haggard",
            "H. Rider Haggard - Allan Quatermain",
            "Allan Quatermain.m4b");
        _ = CreateAudioFile(
            "H. Rider Haggard",
            "H. Rider Haggard - King Solomon's Mines",
            "King Solomon's Mines.m4b");

        var found = Discover(Book("She", "H. Rider Haggard"));

        Assert.Equal([expected], found);
    }

    [Fact]
    public void Discover_BasePathIsBookFolder_NotAuthorFolder()
    {
        var expected = CreateAudioFile(
            "H. Rider Haggard",
            "H. Rider Haggard - She",
            "She.m4b");
        _ = CreateAudioFile(
            "H. Rider Haggard",
            "H. Rider Haggard - Allan Quatermain",
            "Allan Quatermain.m4b");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var discovery = DiscoverResult(Book("She", "H. Rider Haggard"), semantics);

        var basePath = ScanPathPlanner.CalculateBasePath(
            discovery.AttributedFiles,
            semantics,
            discovery.CommonProvenBookBoundary(semantics),
            _root);

        Assert.True(FileSystemPathIdentity.AreEquivalent(
            Path.GetDirectoryName(expected)!,
            basePath,
            semantics));
    }

    [Fact]
    public void Discover_TitleInFolderName_Matches()
    {
        var expected = CreateAudioFile(
            "Jules Verne",
            "Jules Verne - Captain Nemo - Twenty Thousand Leagues Under the Sea",
            "book.m4b");

        var found = Discover(Book(
            "Twenty Thousand Leagues Under the Sea",
            "Jules Verne"));

        Assert.Equal([expected], found);
    }

    [Fact]
    public void Discover_MiddleDecoratedComponentMatchingAnotherTitle_IsNotClaimed()
    {
        _ = CreateAudioFile(
            "Jules Verne",
            "Jules Verne - Captain Nemo - Twenty Thousand Leagues Under the Sea",
            "book.m4b");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;

        var discovery = DiscoverResult(Book("Captain Nemo", "Jules Verne"), semantics);

        Assert.Empty(discovery.AttributedFiles);
        Assert.Null(discovery.CommonProvenBookBoundary(semantics));
    }

    [Theory]
    [InlineData(" - ")]
    [InlineData(" – ")]
    [InlineData(" — ")]
    public void Discover_TitleContainingDelimiter_MatchesTrailingComponentSequence(
        string separator)
    {
        var expected = CreateAudioFile(
            "Story Author",
            $"Story Author{separator}Story Cycle{separator}Love{separator}A Story",
            "book.m4b");

        var found = Discover(Book("Love - A Story", "Story Author"));

        Assert.Equal([expected], found);
    }

    [Fact]
    public void Discover_TitleBeforeTrailingUnrelatedComponent_IsNotClaimed()
    {
        _ = CreateAudioFile(
            "Story Author",
            "Love - A Story - Bonus",
            "book.m4b");

        var found = Discover(Book("Love - A Story", "Story Author"));

        Assert.Empty(found);
    }

    [Fact]
    public void Discover_TitleInFileName_MatchesWhenFolderDoesNotCarryIt()
    {
        var expected = CreateAudioFile(
            "Jules Verne",
            "Audiobooks",
            "Around the World in Eighty Days.mp3");

        var found = Discover(Book(
            "Around the World in Eighty Days",
            "Jules Verne"));

        Assert.Equal([expected], found);
    }

    [Fact]
    public void Discover_MatchedBookFolder_ClaimsEveryFileInFolder()
    {
        var first = CreateAudioFile(
            "Jules Verne",
            "1870 - Twenty Thousand Leagues Under the Sea",
            "Part 01.mp3");
        var second = CreateAudioFile(
            "Jules Verne",
            "1870 - Twenty Thousand Leagues Under the Sea",
            "Part 02.mp3");
        var third = CreateAudioFile(
            "Jules Verne",
            "1870 - Twenty Thousand Leagues Under the Sea",
            "Part 03.mp3");

        var found = Discover(Book(
            "Twenty Thousand Leagues Under the Sea",
            "Jules Verne"));

        Assert.Equal(
            new[] { first, second, third }.OrderBy(path => path, StringComparer.Ordinal),
            found.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void Discover_SiblingBookInSameSeriesFolder_IsNotClaimed()
    {
        _ = CreateAudioFile(
            "Jules Verne",
            "Captain Nemo",
            "1870 - Twenty Thousand Leagues Under the Sea",
            "book.m4b");
        var expected = CreateAudioFile(
            "Jules Verne",
            "Captain Nemo",
            "1874 - The Mysterious Island",
            "book.m4b");

        var found = Discover(Book("The Mysterious Island", "Jules Verne"));

        Assert.Equal([expected], found);
    }

    [Fact]
    public void Discover_PathWithoutTitleOrIdentifier_IsLeftUnmatched()
    {
        _ = CreateAudioFile(
            "Elfie Donnelly",
            "Bibi und Tina",
            "61",
            "61 - track.mp3");

        var found = Discover(Book("Retten die Biber", "Elfie Donnelly"));

        Assert.Empty(found);
    }

    [Fact]
    public void Discover_ShortTitle_DoesNotMatchLongerDecoratedFolderComponent()
    {
        _ = CreateAudioFile(
            "Shared Author",
            "Series - Book Two",
            "part.m4b");

        var found = Discover(Book("Book", "Shared Author"));

        Assert.Empty(found);
    }

    private IReadOnlyList<string> Discover(Audiobook audiobook) =>
        DiscoverResult(audiobook, FileSystemPathSemantics.CurrentHostDefault)
            .AttributedFiles;

    private ScanDiscoveryResult DiscoverResult(
        Audiobook audiobook,
        FileSystemPathSemantics semantics) =>
        ScanFileDiscovery.Discover(
            new LocalFileSystem(),
            _root,
            audiobook,
            Guid.NewGuid(),
            NullLogger.Instance,
            semantics);

    private static Audiobook Book(string title, string author) =>
        new AudiobookBuilder()
            .WithTitle(title)
            .WithAuthor(author)
            .Build();

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
