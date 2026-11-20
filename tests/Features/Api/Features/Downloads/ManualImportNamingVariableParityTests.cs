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
using System.Reflection;
using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "ManualImportNamingVariableParityTests")]
[Trait("Category", "Unit")]
public sealed class ManualImportNamingVariableParityTests : BaseTests
{
    private static ManualImportPathPlanner CreatePlanner(ILogger<FileNamingService>? logger = null) =>
        new(new FileNamingService(
            Mock.Of<IConfigurationService>(),
            logger ?? NullLogger<FileNamingService>.Instance));

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
        string filePattern,
        ILogger<FileNamingService>? logger = null)
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

        var plan = await CreatePlanner(logger).GeneratePathAsync(
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

    // ApplyNamingPattern takes the same branch for a key that is present and blank as for one that
    // is absent: both become the empty sentinel, and the bracket and separator cleanup that
    // follows strips the segment either way. The only difference between the two is a LogWarning.
    // This is the control for the two tests below, and it is the claim an earlier version of this
    // PR's body had backwards.
    [Fact]
    public async Task GeneratePathAsync_AnUnknownValue_RendersTheSameAsAMissingKey()
    {
        var destination = await PlanAsync(
            new Audiobook { Title = "The Wonderful Wizard of Oz" },
            "{Series} - {Title}",
            "{Title}");

        var segments = Segments(destination);

        Assert.Contains("The Wonderful Wizard of Oz", segments);
        Assert.DoesNotContain(segments, segment => segment.StartsWith(" - ", StringComparison.Ordinal));
        Assert.DoesNotContain(segments, segment => segment.Contains("__EMPTY_VAR__", StringComparison.Ordinal));
    }

    // The table is meant to be the one RenameService builds. Reading the keys off rename rather
    // than restating them means this fails when rename grows a token and manual import does not,
    // which is how the two drifted apart in the first place.
    [Fact]
    public async Task GeneratePathAsync_BuildsEveryKeyRenameBuilds_EvenWhenTheValueIsUnknown()
    {
        var renameKeys = RenameNamingKeys();
        Assert.Equal(14, renameKeys.Count);

        var logger = new CapturingLogger<FileNamingService>();
        var everyToken = string.Join(" ", renameKeys.Select(key => $"{{{key}}}"));

        await PlanAsync(
            new Audiobook { Title = "The Wonderful Wizard of Oz" },
            everyToken,
            "{Title}",
            logger);

        // "Variable {VariableName} not found" is the only observable difference between a key that
        // is absent and one that is present and empty, so it is what pins the key set.
        Assert.DoesNotContain(
            logger.Records,
            record => record.Message.Contains("not found in naming pattern", StringComparison.Ordinal));
    }

    // A populated book renders all fourteen without a warning either, so the keys are present on
    // both sides of the known/unknown split rather than only where a value happened to exist.
    [Fact]
    public async Task GeneratePathAsync_EveryKnownValue_RendersWithoutAMissingVariableWarning()
    {
        var logger = new CapturingLogger<FileNamingService>();
        var everyToken = string.Join(" ", RenameNamingKeys().Select(key => $"{{{key}}}"));

        await PlanAsync(CreateSeriesBook(), everyToken, "{Title}", logger);

        Assert.DoesNotContain(
            logger.Records,
            record => record.Message.Contains("not found in naming pattern", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> RenameNamingKeys()
    {
        var builder = typeof(RenameService).GetMethod(
            "BuildNamingVariables",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(builder);

        var table = (Dictionary<string, object>)builder.Invoke(
            null,
            [new Audiobook { Title = "The Wonderful Wizard of Oz" }, null, null, 1, false])!;
        return [.. table.Keys];
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, formatter(state, exception)));
        }
    }
}
