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
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Quality
{
    /// <summary>
    /// The quality profile's <see cref="QualityDefinition.Allowed"/> flag is a gate and
    /// <see cref="QualityProfile.PreferredFormats"/> is a preference. These tests hold the two
    /// apart: a preferred format may move a release's score, and it may never widen the set of
    /// qualities the profile permits.
    /// </summary>
    [Trait("Area", "Search")]
    [Trait("Name", "QualityAllowedGateTests")]
    [Trait("Category", "Application")]
    public sealed class QualityAllowedGateTests : BaseTests
    {
        private static QualityProfileService CreateService()
            => new(Mock.Of<IQualityProfileRepository>(), NullLogger<QualityProfileService>.Instance);

        private static List<string> DefaultPreferredFormats()
            => new() { "m4b", "mp3", "m4a", "flac", "opus" };

        /// <summary>The eleven rungs QualityProfileService seeds into the default profile.</summary>
        private static List<QualityDefinition> SeededLadder(bool allowed = true)
            => new()
            {
                new QualityDefinition { Quality = "AAC 320kbps", Allowed = allowed, Priority = 0 },
                new QualityDefinition { Quality = "AAC 256kbps", Allowed = allowed, Priority = 1 },
                new QualityDefinition { Quality = "AAC 192kbps", Allowed = allowed, Priority = 2 },
                new QualityDefinition { Quality = "AAC 128kbps", Allowed = allowed, Priority = 3 },
                new QualityDefinition { Quality = "AAC 64kbps", Allowed = allowed, Priority = 4 },
                new QualityDefinition { Quality = "MP3 320kbps", Allowed = allowed, Priority = 5 },
                new QualityDefinition { Quality = "MP3 256kbps", Allowed = allowed, Priority = 6 },
                new QualityDefinition { Quality = "MP3 VBR", Allowed = allowed, Priority = 7 },
                new QualityDefinition { Quality = "MP3 192kbps", Allowed = allowed, Priority = 8 },
                new QualityDefinition { Quality = "MP3 128kbps", Allowed = allowed, Priority = 9 },
                new QualityDefinition { Quality = "MP3 64kbps", Allowed = allowed, Priority = 10 }
            };

        private static QualityProfile Profile(List<QualityDefinition> qualities, List<string> preferredFormats)
            => new()
            {
                Qualities = qualities,
                PreferredFormats = preferredFormats,
                PreferredWords = new List<string>(),
                MustNotContain = new List<string>(),
                MustContain = new List<string>(),
                PreferredLanguages = new List<string> { "English" },
                MinimumSeeders = 0,
                MinimumSize = 0,
                MaximumSize = 0,
                MaximumAge = 0
            };

        private static SearchResult Release(string title, string? quality, string? format)
            => new()
            {
                Title = title,
                Quality = quality,
                Format = format ?? string.Empty,
                Language = "English",
                DownloadType = "torrent",
                Seeders = 5,
                Size = 200 * 1024 * 1024,
                PublishedDate = DateTime.UtcNow.ToString("o")
            };

        // ------------------------------------------------------------------ A7

        [Fact]
        public async Task DisallowedQuality_IsRefused_EvenWhenItsNameCarriesAPreferredFormatToken()
        {
            var service = CreateService();
            var profile = Profile(
                new List<QualityDefinition>
                {
                    new() { Quality = "FLAC", Allowed = false, Priority = 0 },
                    new() { Quality = "MP3 320kbps", Allowed = true, Priority = 1 }
                },
                DefaultPreferredFormats());

            // "flac" is in PreferredFormats and "FLAC" contains it, which used to append the token
            // to the allowed list and defeat the veto.
            var disallowed = await service.ScoreSearchResult(Release("Book (FLAC)", "FLAC", "FLAC"), profile);

            // Control: same shape, same substring relationship to a preferred format token, but the
            // profile allows this rung. It must still be accepted.
            var allowed = await service.ScoreSearchResult(Release("Book (MP3)", "MP3 320kbps", "MP3"), profile);

            Assert.True(disallowed.IsRejected, "A quality the profile disallows must be refused");
            Assert.Contains(disallowed.RejectionReasons, reason => reason.Contains("FLAC", StringComparison.Ordinal));
            Assert.False(allowed.IsRejected, $"An allowed quality must still be accepted (score {allowed.TotalScore})");
        }

        [Fact]
        public async Task DisallowingEveryRungInACodecGroup_RefusesThatCodecsContainerLabel()
        {
            var service = CreateService();
            var ladder = SeededLadder();
            foreach (var rung in ladder.Where(q => q.Quality.StartsWith("AAC", StringComparison.Ordinal)))
            {
                rung.Allowed = false;
            }

            var profile = Profile(ladder, DefaultPreferredFormats());

            // "M4B" names no rung, but it is an AAC container and every AAC rung is refused.
            var m4b = await service.ScoreSearchResult(Release("Book (M4B)", "M4B", "M4B"), profile);

            // Control: the MP3 rungs are untouched, so an MP3 release is unaffected.
            var mp3 = await service.ScoreSearchResult(Release("Book (MP3)", "MP3 320kbps", "MP3"), profile);

            Assert.True(m4b.IsRejected, "Refusing every AAC rung must refuse an M4B release");
            Assert.False(mp3.IsRejected, $"MP3 must be unaffected (score {mp3.TotalScore})");
        }

        // --------------------------------------------------------- the AAX regression

        [Fact]
        public async Task ProfileThatRefusesEveryAacRung_RefusesAnAaxRelease()
        {
            // The regression this file exists to stop coming back. AAX reached the gate as a label
            // nothing could place, so it came back NoOpinion and was permitted by a profile that
            // refuses every rung it carries, scoring 88 on the way through.
            //
            // Found by reading the gate's contract, not in a search log: no shipped parser emits
            // "AAX" as a Quality today, so nothing can reach this yet. The scorer already ranks
            // AAX at 95, second only to FLAC (SearchResultScorer.GetQualityScore), which is the
            // branch waiting for the first parser that does.
            var service = CreateService();
            var ladder = SeededLadder(allowed: false);
            var profile = Profile(ladder, DefaultPreferredFormats());

            var aax = await service.ScoreSearchResult(Release("Book (AAX)", "AAX", "AAX"), profile);
            var aaxc = await service.ScoreSearchResult(Release("Book (AAXC)", "AAXC", "AAXC"), profile);
            var mp4 = await service.ScoreSearchResult(Release("Book (MP4)", "MP4", "MP4"), profile);

            // The control that must come out differently, and the reason it is not enough to assert
            // three rejections: a profile refusing everything rejects everything, so the same
            // release has to be accepted by a ladder that allows the AAC rungs.
            var permissive = Profile(SeededLadder(), DefaultPreferredFormats());
            var accepted = await service.ScoreSearchResult(Release("Book (AAX)", "AAX", "AAX"), permissive);

            Assert.True(aax.IsRejected, $"AAX must be refused by a profile that refuses every rung (score {aax.TotalScore})");
            Assert.True(aaxc.IsRejected, $"AAXC must be refused too (score {aaxc.TotalScore})");
            Assert.True(mp4.IsRejected, $"MP4 must be refused too (score {mp4.TotalScore})");
            Assert.False(accepted.IsRejected, $"AAX must still be accepted where the AAC rungs are allowed: {string.Join("; ", accepted.RejectionReasons)}");
        }

        [Fact]
        public async Task AFormatTheGateCannotPlace_IsRefusedOnTheUndeclaredQualityPath()
        {
            // A deliberate consequence rather than a surprise, pinned so it is reviewed: the gate
            // on a release that declares a format but no quality now refuses a format nothing can
            // place.
            //
            // EPUB is a stand-in for the rule, not a field report, and the distinction matters.
            // No shipped provider can currently put an unplaceable format on this path. Every one
            // maps into a closed set first: TorznabResponseParser:135-139 and :370-379,
            // SearchResultAttributeParser.DetectFormatFromTags, and the Internet Archive planner.
            // The one line that could pass a raw indexer filetype through,
            // MyAnonamouseSearchProvider:228, is guarded by Format being empty and the MAM parser
            // always fills it, so it is dead. This pins the gate's contract against a parser that
            // widens later, which is the only way the case arrives.
            var service = CreateService();
            var profile = Profile(SeededLadder(), DefaultPreferredFormats());

            var ebook = await service.ScoreSearchResult(Release("Some Book", null, "EPUB"), profile);

            // Control: a format the gate can place, through the same branch, is unaffected.
            var audiobook = await service.ScoreSearchResult(Release("Some Book", null, "M4B"), profile);

            Assert.True(ebook.IsRejected, $"An unplaceable declared format must be refused (score {ebook.TotalScore})");
            Assert.Contains(ebook.RejectionReasons, reason => reason.Contains("EPUB", StringComparison.Ordinal));
            Assert.False(audiobook.IsRejected, $"A placeable one must not be: {string.Join("; ", audiobook.RejectionReasons)}");
        }

        // ------------------------------------------------- over-correction guard

        [Fact]
        public async Task DefaultShapedProfile_StillAcceptsEverythingItShould()
        {
            var service = CreateService();
            var profile = Profile(SeededLadder(), DefaultPreferredFormats());

            var candidates = new[]
            {
                Release("Book (MP3 320)", "MP3 320kbps", "MP3"),
                Release("Book (MP3 64)", "MP3 64kbps", "MP3"),
                Release("Book (AAC 256)", "AAC 256kbps", "AAC"),
                Release("Book (M4B)", "M4B", "M4B"),
                Release("Book (FLAC)", "FLAC", "FLAC"),
                Release("Book (no quality)", null, "M4B"),
                Release("Book (nothing declared)", null, null)
            };

            foreach (var candidate in candidates)
            {
                var score = await service.ScoreSearchResult(candidate, profile);
                Assert.False(
                    score.IsRejected,
                    $"'{candidate.Title}' must not be rejected by a default-shaped profile: {string.Join("; ", score.RejectionReasons)}");

                // Positive assertions, because an inert scorer returning an empty result would
                // satisfy every IsRejected check above and prove nothing.
                Assert.True(score.TotalScore > 0, $"'{candidate.Title}' scored {score.TotalScore}");
                Assert.NotEmpty(score.ScoreBreakdown);
            }
        }

        [Fact]
        public async Task CuratedProfile_RefusesTheRungItNamesAndNothingElse()
        {
            // The guard above pins the stock default, where every rung is allowed and the gate can
            // only ever return allowed or no opinion. This one puts a live refusal in the ladder,
            // which is the shape where over-correction would actually show up.
            var service = CreateService();
            var ladder = SeededLadder();
            ladder.Single(rung => rung.Quality == "MP3 64kbps").Allowed = false;
            var profile = Profile(ladder, DefaultPreferredFormats());

            var refused = await service.ScoreSearchResult(Release("Book (MP3 64)", "MP3 64kbps", "MP3"), profile);
            var neighbour = await service.ScoreSearchResult(Release("Book (MP3 128)", "MP3 128kbps", "MP3"), profile);
            var otherCodec = await service.ScoreSearchResult(Release("Book (AAC 64)", "AAC 64kbps", "AAC"), profile);
            var unlisted = await service.ScoreSearchResult(Release("Book (FLAC)", "FLAC", "FLAC"), profile);

            Assert.True(refused.IsRejected, "The refused rung must be refused");
            Assert.False(neighbour.IsRejected, $"The rung next to it must not be: {string.Join("; ", neighbour.RejectionReasons)}");
            Assert.False(otherCodec.IsRejected, $"The same bitrate in another codec must not be: {string.Join("; ", otherCodec.RejectionReasons)}");
            Assert.False(unlisted.IsRejected, $"A codec the ladder does not list must not be: {string.Join("; ", unlisted.RejectionReasons)}");
        }

        [Fact]
        public async Task ProfileWithNoQualityLadder_GatesNothing()
        {
            var service = CreateService();
            var profile = Profile(new List<QualityDefinition>(), DefaultPreferredFormats());

            var score = await service.ScoreSearchResult(Release("Book (FLAC)", "FLAC", "FLAC"), profile);

            Assert.False(score.IsRejected, "An empty ladder expresses no opinion and must gate nothing");
        }

        // ------------------------------------------------------------------ A6

        [Fact]
        public async Task UndeclaredQuality_IsGatedOnTheFormatTheReleaseDidDeclare()
        {
            var service = CreateService();
            var profile = Profile(
                new List<QualityDefinition>
                {
                    new() { Quality = "FLAC", Allowed = false, Priority = 0 },
                    new() { Quality = "AAC 256kbps", Allowed = true, Priority = 1 }
                },
                new List<string> { "m4b", "flac" });

            // The A6 case: no quality label, but the release says it is FLAC and FLAC is refused.
            var undeclaredFlac = await service.ScoreSearchResult(Release("Probe Book Four", null, "FLAC"), profile);

            // Its declared twin, which already behaved correctly. After the fix the two agree.
            var declaredFlac = await service.ScoreSearchResult(Release("Probe Book Four FLAC", "FLAC", "FLAC"), profile);

            Assert.True(undeclaredFlac.IsRejected, "A release that declares FLAC as its format must be gated on FLAC");
            Assert.True(declaredFlac.IsRejected, "Its declared twin must be refused too");
        }

        // ------------------------------------------------ deliberate non-changes

        [Fact]
        public async Task PreferredFormats_StillMoveTheScore_WithoutMovingTheGate()
        {
            var service = CreateService();
            var profile = Profile(SeededLadder(), new List<string> { "m4b" });

            var preferred = await service.ScoreSearchResult(Release("Book (M4B)", "AAC 256kbps", "M4B"), profile);
            var notPreferred = await service.ScoreSearchResult(Release("Book (MP3)", "MP3 256kbps", "MP3"), profile);

            Assert.False(preferred.IsRejected, "A preferred format is a preference, not a gate");
            Assert.False(notPreferred.IsRejected, "An unpreferred format is still permitted by an allowing profile");
            Assert.True(
                preferred.TotalScore > notPreferred.TotalScore,
                $"The preference must still be worth score ({preferred.TotalScore} vs {notPreferred.TotalScore})");
        }

        [Fact]
        public async Task UsenetResults_AreLeftExactlyAsTheyWere()
        {
            var service = CreateService();
            var profile = Profile(
                new List<QualityDefinition> { new() { Quality = "FLAC", Allowed = false, Priority = 0 } },
                DefaultPreferredFormats());

            var usenet = Release("Book (FLAC)", "FLAC", "FLAC");
            usenet.DownloadType = "nzb";
            usenet.NzbUrl = "http://indexer.invalid/book.nzb";

            var nzbScore = await service.ScoreSearchResult(usenet, profile);

            // The torrent twin, which the profile does refuse. The two differing is the point:
            // every profile gate in this scorer already sits inside a torrent-only branch, and
            // moving them is a separate, unmeasured change.
            var torrentScore = await service.ScoreSearchResult(Release("Book (FLAC)", "FLAC", "FLAC"), profile);

            Assert.False(nzbScore.IsRejected, "Usenet results keep their existing exemption from the quality gate");
            Assert.True(torrentScore.IsRejected, "The torrent twin is refused");
        }

        [Fact]
        public async Task ReleaseThatDeclaresNothingAtAll_IsStillAccepted()
        {
            var service = CreateService();
            var profile = Profile(
                new List<QualityDefinition>
                {
                    new() { Quality = "FLAC", Allowed = false, Priority = 0 },
                    new() { Quality = "AAC 256kbps", Allowed = true, Priority = 1 }
                },
                new List<string> { "m4b", "flac" });

            // The control that must come out differently: an indexer that labels nothing is not
            // making a claim the profile can refuse, so it stays in the pool, penalised.
            var anonymous = await service.ScoreSearchResult(Release("Some Book", null, null), profile);

            // And a format the profile allows is accepted, so the gate is not simply refusing
            // every undeclared quality.
            var undeclaredM4b = await service.ScoreSearchResult(Release("Some Book M4B", null, "M4B"), profile);

            Assert.False(anonymous.IsRejected, $"An unlabelled release must not be refused: {string.Join("; ", anonymous.RejectionReasons)}");
            Assert.True(anonymous.ScoreBreakdown.ContainsKey("QualityMissing"), "An unlabelled release must still take the missing-quality penalty");
            Assert.False(undeclaredM4b.IsRejected, $"An allowed format must be accepted: {string.Join("; ", undeclaredM4b.RejectionReasons)}");
        }
    }
}
