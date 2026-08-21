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
        Series = "Oz",
        SeriesNumber = "1",
        Asin = "B007BR5KZA"
    };

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

        Assert.Contains("L. Frank Baum", destination, StringComparison.Ordinal);
        Assert.Contains("Oz", destination, StringComparison.Ordinal);
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

        Assert.Contains("1", destination, StringComparison.Ordinal);
        Assert.DoesNotContain("SeriesNumber", destination, StringComparison.OrdinalIgnoreCase);
    }

    // Deliberately not asserted here: whether an absent key should instead be inserted empty.
    // A missing variable yields a sentinel that FileNamingService then cleans up, stripping
    // brackets and adjacent separators, which an empty string does not get. So inserting empties
    // would turn "{Series} - {Title}" into " - Title" where today it renders "Title". That
    // divergence from RenameService is real but the behaviour here looks like the better one, so
    // it is described in the issue rather than changed.
}
