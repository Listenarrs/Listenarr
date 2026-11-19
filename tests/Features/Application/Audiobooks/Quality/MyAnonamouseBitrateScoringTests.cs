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
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Quality
{
    /// <summary>
    /// The quality gate judges whatever the MyAnonamouse parser classified. These tests run an item
    /// through the parser and then through the scorer, so a bitrate label that names the wrong
    /// codec family shows up as a refusal of a release the profile allows.
    /// </summary>
    [Trait("Area", "Search")]
    [Trait("Name", "MyAnonamouseBitrateScoringTests")]
    [Trait("Category", "Application")]
    public sealed class MyAnonamouseBitrateScoringTests : BaseTests
    {
        private const string BookTitle = "A Princess of Mars";

        private static SearchResult ParseOne(string tags, string? filetype = null, string description = "Read in full.")
        {
            var payload = JsonSerializer.Serialize(new
            {
                data = new[] { new
                {
                    id = "1", title = BookTitle, author_info = "{\"1\":\"Edgar Rice Burroughs\"}",
                    tags, filetype, description, size = 209715200, seeders = 5
                } }
            });
            var indexer = new Indexer { Id = 42, Name = "fixture", Url = "http://fixture.invalid", Implementation = "MyAnonamouse", Type = "Torrent" };
            var result = SearchResultConverters.ToSearchResult(Assert.Single(MyAnonamouseResponseParser.Parse(payload, indexer, NullLogger.Instance)));
            result.Language = "English";
            result.PublishedDate = DateTime.UtcNow.ToString("o");
            return result;
        }

        private static QualityProfile Profile(params (string Quality, bool Allowed)[] rungs)
            => new()
            {
                Qualities = rungs.Select((rung, index) => new QualityDefinition { Quality = rung.Quality, Allowed = rung.Allowed, Priority = index }).ToList(),
                PreferredFormats = new List<string>(),
                PreferredWords = new List<string>(),
                MustNotContain = new List<string>(),
                MustContain = new List<string>(),
                PreferredLanguages = new List<string> { "English" },
                MinimumSeeders = 0,
                MinimumSize = 0,
                MaximumSize = 0,
                MaximumAge = 0
            };

        [Theory]
        [InlineData("M4B 64kbps", null, "M4B", "AAC 64kbps")]
        [InlineData("AAC 64kbps", null, "AAC", "AAC 64kbps")]
        [InlineData("OPUS 64kbps", null, "OPUS", "OPUS 64kbps")]
        [InlineData("64kbps", "m4b", "M4B", "AAC 64kbps")]
        [InlineData("MP3 64kbps", null, "MP3", "MP3 64kbps")]
        // Description text that merely contains a codec word changes Format (unchanged behaviour) but not the declared MP3 label.
        [InlineData("MP3 128kbps", null, "AAC", "MP3 128kbps", "Isaac reads every chapter.")]
        [InlineData("MP3 128kbps", null, "OPUS", "MP3 128kbps", "A magnum opus")]
        // Tags that name no codec declare nothing, so the codec the description names labels the bitrate.
        [InlineData("64kbps", null, "AAC", "AAC 64kbps", "Encoded as AAC 64kbps")]
        [InlineData("", null, "AAC", "AAC 64kbps", "AAC 64kbps")]
        [InlineData("128kbps", null, "M4B", "AAC 128kbps", "m4b release")]
        // A filetype that names MP3 declares it even when the tags name no codec.
        [InlineData("64kbps", "mp3", "AAC", "MP3 64kbps", "Encoded as AAC 64kbps")]
        public async Task DeclaredCodec_KeepsItsFamilyInTheBitrateLabel_AndIsAcceptedWhereThatRungIsAllowed(
            string tags, string? filetype, string expectedFormat, string expectedQuality, string description = "Read in full.")
        {
            // Control rows: the MP3 row must keep its MP3 label while every other family is judged
            // on its own rung, with the MP3 rung at the same bitrate disabled.
            var kbps = expectedQuality.Split(' ')[1];
            var other = (expectedQuality.StartsWith("MP3") ? "AAC " : "MP3 ") + kbps;
            var profile = Profile((expectedQuality, true), (other, false));
            var service = new QualityProfileService(Mock.Of<IQualityProfileRepository>(), NullLogger<QualityProfileService>.Instance);

            var result = ParseOne(tags, filetype, description);
            var score = await service.ScoreSearchResult(result, profile);

            Assert.Equal(expectedFormat, result.Format);
            Assert.Equal(expectedQuality, result.Quality);
            Assert.False(score.IsRejected, $"A {tags} release must be accepted: {string.Join("; ", score.RejectionReasons)}");
        }
    }
}
