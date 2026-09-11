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
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportNamingVariableParityTests")]
[Trait("Category", "Unit")]
public sealed class ManualImportNamingVariableParityTests : BaseTests
{
    private static ManualImportPathPlanner CreatePlanner() =>
        new(new FileNamingService(
            Mock.Of<IConfigurationService>(),
            NullLogger<FileNamingService>.Instance));

    private static Audiobook CreateSeriesBook() => new()
    {
        Title = "The Wonderful Wizard of Oz",
        Authors = ["L. Frank Baum"],
        // The series name must not be a substring of the title. It was "Oz", and an assertion
        // that the destination contains "Oz" then passed whenever the title rendered, whatever
        // the series token did. SeriesFixture_CannotPassOnTheTitleAlone guards the property.
        Series = "Land of Oz",
        SeriesNumber = "1",
        Quality = "M4B 128kbps",
        Asin = "B007BR5KZA"
    };

    // Split the destination into path components so an assertion names a whole segment. Asserting
    // a substring of the whole path passes for the wrong reason as soon as some other segment
    // happens to contain the text: a bare "1" matches a root folder id, a disk number or a
    // sequence suffix.
    private static string[] Segments(string destination) =>
        destination.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void SeriesFixture_CannotPassOnTheTitleAlone()
    {
        var book = CreateSeriesBook();

        Assert.NotNull(book.Series);
        Assert.NotNull(book.Title);
        Assert.NotNull(book.SeriesNumber);
        Assert.NotNull(book.Quality);

        // Every token asserted below has to be absent from the other fields, or its assertion
        // proves nothing about the token it names.
        Assert.DoesNotContain(book.Series, book.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(book.SeriesNumber, book.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(book.Quality, book.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(book.Series, book.Authors[0], StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> PlanAsync(
        Audiobook audiobook,
        string folderPattern,
        string filePattern)
    {
        var settings = new ApplicationSettingsBuilder()
            .WithOutputPath("/library")
            .Build();
        settings.FolderNamingPattern = folderPattern;
        settings.FileNamingPattern = filePattern;

        var item = new ManualImportItemDto
        {
            FullPath = "/incoming/book.m4b",
            MatchedAudiobookId = 1
        };

        var plan = await CreatePlanner().GeneratePathAsync(
            audiobook,
            audiobook.CreateBasicAudioMetadata(),
            item,
            "/library",
            [new RootFolder { Id = 1, Name = "Library", Path = "/library" }],
            settings,
            new FileSystemPathSemantics(
                FileSystemPathSyntax.Unix,
                FileSystemCaseSensitivity.Sensitive));
        return plan.DestinationPath;
    }

    // RenameService.BuildNamingVariables keys its dictionary with StringComparer.OrdinalIgnoreCase
    // and the token regex in FileNamingService is case-insensitive, so a pattern written in any
    // case resolves under rename. A case-sensitive dictionary here made manual import the one path
    // where {series} and {ASIN} silently produced nothing.
    [Theory]
    [InlineData("{Author}/{Series}", "{Title}")]
    [InlineData("{author}/{series}", "{title}")]
    [InlineData("{AUTHOR}/{SERIES}", "{TITLE}")]
    public async Task GeneratePathAsync_TokenCasing_DoesNotChangeTheResult(
        string folderPattern,
        string filePattern)
    {
        var destination = await PlanAsync(CreateSeriesBook(), folderPattern, filePattern);
        var segments = Segments(destination);

        // Whole path components, not substrings: with a case-sensitive dictionary the lookup
        // misses, the sentinel cleanup strips the segment, and the segment is simply gone.
        Assert.Contains("L. Frank Baum", segments);
        Assert.Contains("Land of Oz", segments);
    }

    // SeriesNumber and Quality were absent from this table entirely while being present in the
    // rename and library-add tables, so a pattern using either rendered one way through rename and
    // lost the segment through manual import.
    [Fact]
    public async Task GeneratePathAsync_SeriesNumberToken_IsRendered()
    {
        var destination = await PlanAsync(
            CreateSeriesBook(),
            "{Author}/{Series}/{SeriesNumber}",
            "{Title}");

        var segments = Segments(destination);

        // Its own path component. "1" as a substring of the whole path would also match a root
        // folder id or a disk number, so the assertion would survive the key being dropped.
        Assert.Contains("1", segments);
        Assert.DoesNotContain("SeriesNumber", destination, StringComparison.OrdinalIgnoreCase);
    }

    // Quality is the other key this PR adds, and it was asserted nowhere: the summary claimed two
    // tokens and the tests covered one.
    [Fact]
    public async Task GeneratePathAsync_QualityToken_IsRendered()
    {
        var destination = await PlanAsync(
            CreateSeriesBook(),
            "{Author}/{Series}/{Quality}",
            "{Title}");

        var segments = Segments(destination);

        Assert.Contains("M4B 128kbps", segments);
        Assert.DoesNotContain("Quality", destination, StringComparison.OrdinalIgnoreCase);
    }

    // Both added keys resolve through a lowercase pattern too, which is the intersection of the
    // two defects this PR fixes: a key that is present but unreachable is no better than an
    // absent one.
    [Fact]
    public async Task GeneratePathAsync_AddedTokens_ResolveInAnyCase()
    {
        var destination = await PlanAsync(
            CreateSeriesBook(),
            "{author}/{seriesnumber}/{quality}",
            "{title}");

        var segments = Segments(destination);

        Assert.Contains("L. Frank Baum", segments);
        Assert.Contains("1", segments);
        Assert.Contains("M4B 128kbps", segments);
    }

    // Deliberately not asserted here: whether an absent key should instead be inserted empty.
    // A missing variable yields a sentinel that FileNamingService then cleans up, stripping
    // brackets and adjacent separators, which an empty string does not get. So inserting empties
    // would turn "{Series} - {Title}" into " - Title" where today it renders "Title". That
    // divergence from RenameService is real but the behaviour here looks like the better one, so
    // it is described in the issue rather than changed.
}
