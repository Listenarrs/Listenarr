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
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_DuplicatesTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_DuplicatesTests : BaseTests
    {
        private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

        private async Task<Audiobook> AddBookAsync(
            string title,
            string? author = null,
            string? asin = null,
            string? subtitle = null,
            string? year = null,
            string? folder = null)
        {
            var builder = new AudiobookBuilder().WithTitle(title);
            if (author != null) builder = builder.WithAuthor(author);
            if (subtitle != null) builder = builder.WithSubtitle(subtitle);
            if (year != null) builder = builder.WithYear(year);
            builder = builder.WithBasePath(FileService.GetTempDirectory(folder ?? $"dupes-{Guid.NewGuid():N}"));
            var book = builder.Build();
            book.Asin = asin;
            return await _audiobookRepository.AddAsync(book);
        }

        private async Task AddFileAsync(Audiobook owner, string fileName)
        {
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(owner)
                .WithPath(Path.Join(owner.BasePath, fileName))
                .Build());
        }

        private async Task<JsonElement> SweepAsync()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.GetDuplicates(CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(result);
            return ToJson(ok.Value);
        }

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "SameAsin_FormsGroup")]
        public async Task GetDuplicates_SameAsin_FormsGroup()
        {
            var a = await AddBookAsync("Warbreaker", "Brandon Sanderson", asin: "B002UZZ7Y2");
            var b = await AddBookAsync("Warbreaker (Unabridged)", "Brandon Sanderson", asin: "b002uzz7y2");

            var payload = await SweepAsync();
            var groups = payload.GetProperty("duplicateGroups").EnumerateArray().ToList();
            var group = Assert.Single(groups);
            Assert.Equal("asin", group.GetProperty("reason").GetString());
            var ids = group.GetProperty("books").EnumerateArray()
                .Select(x => x.GetProperty("id").GetInt32()).OrderBy(x => x).ToList();
            Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x).ToList(), ids);
        }

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "SameTitleAuthor_NoAsins_FormsGroup")]
        public async Task GetDuplicates_SameTitleAuthor_NoAsins_FormsGroup()
        {
            await AddBookAsync("The Ghost Brigades", "John Scalzi");
            await AddBookAsync("The Ghost  Brigades!", "John Scalzi");

            var payload = await SweepAsync();
            var groups = payload.GetProperty("duplicateGroups").EnumerateArray().ToList();
            var group = Assert.Single(groups);
            Assert.Equal("title-author", group.GetProperty("reason").GetString());
            Assert.Equal(2, group.GetProperty("books").GetArrayLength());
        }

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "DistinctAsins_AreNotDuplicates")]
        public async Task GetDuplicates_DistinctAsins_AreNotDuplicates()
        {
            await AddBookAsync("Dune", "Frank Herbert", asin: "B0011UGNDG");
            await AddBookAsync("Dune", "Frank Herbert", asin: "B08G9PRS1K");

            var payload = await SweepAsync();
            Assert.Empty(payload.GetProperty("duplicateGroups").EnumerateArray());
        }

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "DifferentSubtitleAndYear_AreNotDuplicates")]
        public async Task GetDuplicates_DifferentSubtitleAndYear_AreNotDuplicates()
        {
            await AddBookAsync("Foundation", "Isaac Asimov", subtitle: "Book One", year: "1951");
            await AddBookAsync("Foundation", "Isaac Asimov", subtitle: "The Radio Dramatization", year: "1973");

            var payload = await SweepAsync();
            Assert.Empty(payload.GetProperty("duplicateGroups").EnumerateArray());
        }

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "SuggestedKeeper_PrefersMostFiles_ThenOldestId")]
        public async Task GetDuplicates_SuggestedKeeper_PrefersMostFiles_ThenOldestId()
        {
            var empty = await AddBookAsync("Old Man's War", "John Scalzi");
            var withFiles = await AddBookAsync("Old Man's War.", "John Scalzi");
            await AddFileAsync(withFiles, "part1.mp3");
            await AddFileAsync(withFiles, "part2.mp3");

            var payload = await SweepAsync();
            var group = Assert.Single(payload.GetProperty("duplicateGroups").EnumerateArray().ToList());
            Assert.Equal(withFiles.Id, group.GetProperty("suggestedKeeperId").GetInt32());
            Assert.True(empty.Id < withFiles.Id); // keeper won on files, not id

            // Tie on files (none anywhere) → oldest id wins.
            var first = await AddBookAsync("Agent to the Stars", "John Scalzi");
            await AddBookAsync("Agent to the Stars!", "John Scalzi");
            var payload2 = await SweepAsync();
            var tieGroup = payload2.GetProperty("duplicateGroups").EnumerateArray()
                .Single(g => g.GetProperty("key").GetString()!.Contains("agent"));
            Assert.Equal(first.Id, tieGroup.GetProperty("suggestedKeeperId").GetInt32());
        }

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "TwoFilenameSchemes_FlagDuplicateCopies")]
        public async Task GetDuplicates_TwoFilenameSchemes_FlagDuplicateCopies()
        {
            var book = await AddBookAsync("Shadow of the Giant", "Orson Scott Card", folder: "dupes-copies");
            // Same stem, two numbering styles — the signature-identity case.
            await AddFileAsync(book, "Shadow of the Giant-01.mp3");
            await AddFileAsync(book, "Shadow of the Giant-02.mp3");
            await AddFileAsync(book, "Shadow of the Giant (1).mp3");
            await AddFileAsync(book, "Shadow of the Giant (2).mp3");

            var payload = await SweepAsync();
            var hits = payload.GetProperty("duplicateCopyBooks").EnumerateArray().ToList();
            var hit = Assert.Single(hits);
            Assert.Equal(book.Id, hit.GetProperty("id").GetInt32());
            Assert.Equal(4, hit.GetProperty("fileCount").GetInt32());
        }

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "SingleScheme_NotFlagged")]
        public async Task GetDuplicates_SingleScheme_NotFlagged()
        {
            var book = await AddBookAsync("Friday", "Robert A. Heinlein", folder: "dupes-single");
            await AddFileAsync(book, "friday-01_77.mp3");
            await AddFileAsync(book, "friday-02_77.mp3");
            await AddFileAsync(book, "friday-03_77.mp3");

            var payload = await SweepAsync();
            Assert.Empty(payload.GetProperty("duplicateCopyBooks").EnumerateArray());
        }
    }
}
