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
using System.Net;
using System.Text;
using Listenarr.Application.Search.Indexers.Torznab;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Quality
{
    /// <summary>
    /// The quality gate judges whatever the Torznab parsers classified. These tests run a feed item
    /// through each parser and then through the scorer, so a declared filetype/format attribute
    /// that a title or description guess overwrites shows up as a refusal here. Both parsers carry
    /// the same logic and are held to the same expectations.
    /// </summary>
    [Trait("Area", "Search")]
    [Trait("Name", "TorznabDeclaredAttributeScoringTests")]
    [Trait("Category", "Application")]
    public sealed class TorznabDeclaredAttributeScoringTests : BaseTests
    {
        private const string BookTitle = "A Princess of Mars";

        private sealed class FeedHandler : HttpMessageHandler
        {
            private readonly string _xml;

            public FeedHandler(string xml) => _xml = xml;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_xml, Encoding.UTF8, "application/xml")
                });
        }

        public static TheoryData<string> Parsers => new() { "application", "infrastructure" };

        private static string Feed(string title, string description, string format)
            => $@"<?xml version=""1.0""?>
<rss>
  <channel>
    <item>
      <title>{title}</title>
      <guid>fixture-guid</guid>
      <pubDate>Mon, 01 Jan 2025 00:00:00 +0000</pubDate>
      <description>{description}</description>
      <torznab:attr xmlns:torznab=""http://torznab.com/schemas/2015/feed"" name=""format"" value=""{format}"" />
      <torznab:attr xmlns:torznab=""http://torznab.com/schemas/2015/feed"" name=""seeders"" value=""5"" />
      <enclosure url=""https://example.invalid/fixture.torrent"" length=""209715200"" />
    </item>
  </channel>
</rss>";

        private static async Task<SearchResult> ParseOne(string parser, string xml)
        {
            var indexer = new Indexer
            {
                Name = "fixture",
                Url = "https://example.invalid",
                Type = "Torrent",
                Implementation = "torznab"
            };

            List<IndexerSearchResult> parsed = parser == "application"
                ? await new TorznabResponseParser(new HttpClient(), NullLogger.Instance).ParseAsync(xml, indexer)
                : await new TorznabNewznabSearchProvider(
                    new HttpClient(new FeedHandler(xml)),
                    NullLogger<TorznabNewznabSearchProvider>.Instance).SearchAsync(indexer, BookTitle);

            var item = Assert.Single(parsed);
            var result = SearchResultConverters.ToSearchResult(item);
            result.Language = "English";
            result.PublishedDate = DateTime.UtcNow.ToString("o");
            return result;
        }

        private static QualityProfileService CreateService()
            => new(Mock.Of<IQualityProfileRepository>(), NullLogger<QualityProfileService>.Instance);

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
        [MemberData(nameof(Parsers))]
        public async Task DeclaredFormat_OutranksATitleGuess_AndScoresLikeAnOrdinaryTitle(string parser)
        {
            // A profile saved from the UI: the MP3 rungs are ticked, no OPUS rung exists and
            // PreferredFormats is empty.
            var profile = Profile(("MP3 320kbps", true), ("MP3 128kbps", true));
            var service = CreateService();

            // "opus" appears in the description as an ordinary word. The indexer declared MP3.
            var collides = await ParseOne(parser, Feed(BookTitle, "The author&apos;s magnum opus, read in full.", "mp3"));
            // Control: the same attributes with a description that guesses nothing.
            var control = await ParseOne(parser, Feed(BookTitle, "The author&apos;s best known novel, read in full.", "mp3"));

            var collidesScore = await service.ScoreSearchResult(collides, profile);
            var controlScore = await service.ScoreSearchResult(control, profile);

            Assert.Equal("MP3", collides.Format);
            Assert.Equal("MP3", control.Format);
            Assert.False(collidesScore.IsRejected, $"A declared MP3 must not be refused: {string.Join("; ", collidesScore.RejectionReasons)}");
            Assert.Equal(controlScore.TotalScore, collidesScore.TotalScore);
        }

        [Theory]
        [MemberData(nameof(Parsers))]
        public async Task DeclaredFormat_IsNotContradictedByAFormatWordInTheDescription(string parser)
        {
            var profile = Profile(("MP3 320kbps", true), ("MP3 128kbps", true));
            var service = CreateService();

            // The description names other containers in passing; the indexer declared MP3.
            var flac = await ParseOne(parser, Feed(BookTitle, "Not a flac rip. Read in full.", "mp3"));
            var m4b = await ParseOne(parser, Feed(BookTitle, "Also sold as m4b. Read in full.", "mp3"));
            var control = await ParseOne(parser, Feed(BookTitle, "Read in full.", "mp3"));

            var controlScore = await service.ScoreSearchResult(control, profile);
            foreach (var release in new[] { flac, m4b })
            {
                var score = await service.ScoreSearchResult(release, profile);
                Assert.Equal("MP3", release.Format);
                Assert.Equal(control.Quality, release.Quality);
                Assert.False(score.IsRejected, $"A declared MP3 must not be refused: {string.Join("; ", score.RejectionReasons)}");
                Assert.Equal(controlScore.TotalScore, score.TotalScore);
            }
        }

        [Theory]
        [MemberData(nameof(Parsers))]
        public async Task DeclaredFlac_KeepsItsQualityLabel_WhenTheTextAgrees(string parser)
        {
            var profile = Profile(("MP3 320kbps", true), ("FLAC", true));
            var service = CreateService();

            var declared = await ParseOne(parser, Feed($"{BookTitle} [FLAC]", "Read in full.", "flac"));
            // Control: the same release with no usable attribute, labelled from the text alone.
            var undeclared = await ParseOne(parser, Feed($"{BookTitle} [FLAC]", "Read in full.", "unlisted"));

            var declaredScore = await service.ScoreSearchResult(declared, profile);
            var undeclaredScore = await service.ScoreSearchResult(undeclared, profile);

            Assert.Equal("FLAC", declared.Format);
            Assert.Equal("FLAC", declared.Quality);
            Assert.Equal(undeclared.Quality, declared.Quality);
            Assert.Equal(undeclaredScore.TotalScore, declaredScore.TotalScore);
        }

        [Theory]
        [MemberData(nameof(Parsers))]
        public async Task TextGuess_StillFillsAFormatTheIndexerDidNotDeclare(string parser)
        {
            // The guess is the fallback, not removed: with no usable attribute it still decides.
            var guessed = await ParseOne(parser, Feed(BookTitle, "Read in full, encoded as opus.", "unlisted"));

            Assert.Equal("OPUS", guessed.Format);
        }

        [Theory]
        [MemberData(nameof(Parsers))]
        public async Task DeclaredAac_KeepsItsCodecInTheBitrateLabel_AndIsAcceptedWhereAac128IsAllowed(string parser)
        {
            var profile = Profile(("AAC 128kbps", true), ("MP3 128kbps", false), ("MP3 64kbps", true));
            var service = CreateService();

            var aac = await ParseOne(parser, Feed($"{BookTitle} 128kbps", "Read in full.", "aac"));
            // Control: the same title declared as MP3 is labelled MP3 128kbps and the profile does
            // refuse that rung, so a pass above cannot be the gate being absent.
            var mp3 = await ParseOne(parser, Feed($"{BookTitle} 128kbps", "Read in full.", "mp3"));

            var aacScore = await service.ScoreSearchResult(aac, profile);
            var mp3Score = await service.ScoreSearchResult(mp3, profile);

            Assert.Equal("AAC", aac.Format);
            Assert.Equal("AAC 128kbps", aac.Quality);
            Assert.False(aacScore.IsRejected, $"A declared AAC 128kbps must be accepted: {string.Join("; ", aacScore.RejectionReasons)}");
            Assert.Equal("MP3 128kbps", mp3.Quality);
            Assert.True(mp3Score.IsRejected, "The disabled MP3 128kbps rung must still refuse an MP3 release");
        }

        [Theory]
        [MemberData(nameof(Parsers))]
        public async Task DeclaredAacFiletype_CarriesItsCodecIntoTheLabel(string parser)
        {
            var aac = await ParseOne(parser, Feed(BookTitle, "Read in full.", "aac 192kbps"));

            Assert.Equal("AAC 192kbps", aac.Quality);
        }
    }
}
