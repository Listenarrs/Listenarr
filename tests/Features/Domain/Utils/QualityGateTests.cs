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

namespace Listenarr.Tests.Features.Domain.Utils
{
    /// <summary>
    /// The gate itself, away from the scorer. These pin the three rules and, just as importantly,
    /// the line between the absence that reads as silence and the absence that reads as refusal.
    /// </summary>
    [Trait("Area", "Search")]
    [Trait("Name", "QualityGateTests")]
    [Trait("Category", "Domain")]
    public sealed class QualityGateTests : BaseTests
    {
        private static QualityProfile WithRungs(params (string Quality, bool Allowed)[] rungs)
            => new()
            {
                Qualities = rungs
                    .Select((rung, index) => new QualityDefinition
                    {
                        Quality = rung.Quality,
                        Allowed = rung.Allowed,
                        Priority = index
                    })
                    .ToList()
            };

        [Fact]
        public void NoLadder_MeansNoOpinion()
        {
            Assert.Equal(QualityGateVerdict.NoOpinion, QualityGate.Evaluate("FLAC", null));
            Assert.Equal(QualityGateVerdict.NoOpinion, QualityGate.Evaluate("FLAC", new QualityProfile()));
            Assert.Equal(QualityGateVerdict.NoOpinion, QualityGate.Evaluate(null, WithRungs(("FLAC", false))));
            Assert.Equal(QualityGateVerdict.NoOpinion, QualityGate.Evaluate("   ", WithRungs(("FLAC", false))));
        }

        [Fact]
        public void ARungNamingTheLabel_DecidesIt()
        {
            var profile = WithRungs(("MP3 320kbps", true), ("MP3 64kbps", false));

            Assert.Equal(QualityGateVerdict.Allowed, QualityGate.Evaluate("MP3 320kbps", profile));
            Assert.Equal(QualityGateVerdict.Refused, QualityGate.Evaluate("MP3 64kbps", profile));
            Assert.Equal(QualityGateVerdict.Allowed, QualityGate.Evaluate("mp3 320KBPS", profile));
        }

        [Fact]
        public void ABroaderLabelCoveringSeveralRungs_TakesThePermissiveReading()
        {
            var profile = WithRungs(("MP3 320kbps", true), ("MP3 64kbps", false));

            // "MP3" names both rungs, and the release could be either of them.
            Assert.Equal(QualityGateVerdict.Allowed, QualityGate.Evaluate("MP3", profile));

            // The control: with every rung it names refused, the same label is refused.
            Assert.Equal(
                QualityGateVerdict.Refused,
                QualityGate.Evaluate("MP3", WithRungs(("MP3 320kbps", false), ("MP3 64kbps", false))));
        }

        [Fact]
        public void ANameMatch_ShortCircuitsTheCodecGroup()
        {
            // "MP3 64kbps" names a refused rung. Its codec group also holds an allowed rung, and
            // the name match has to win or per-rung refusal would mean nothing.
            var profile = WithRungs(("MP3 320kbps", true), ("MP3 64kbps", false));

            Assert.Equal(QualityGateVerdict.Refused, QualityGate.Evaluate("MP3 64kbps", profile));
        }

        [Fact]
        public void ALabelNamingNoRung_FallsBackToItsCodecGroup()
        {
            // "M4B" is an AAC container and names no rung of its own.
            Assert.Equal(
                QualityGateVerdict.Allowed,
                QualityGate.Evaluate("M4B", WithRungs(("AAC 256kbps", true), ("AAC 64kbps", false))));

            Assert.Equal(
                QualityGateVerdict.Refused,
                QualityGate.Evaluate("M4B", WithRungs(("AAC 256kbps", false), ("AAC 64kbps", false))));

            // The control: a codec the ladder does not carry is not refused by it.
            Assert.Equal(
                QualityGateVerdict.NoOpinion,
                QualityGate.Evaluate("M4B", WithRungs(("MP3 320kbps", false))));
        }

        [Fact]
        public void TheCodecGroupFallback_WorksAtCodecGranularityAndThatIsVisible()
        {
            // Documented consequence, pinned so it is a decision rather than a surprise: the
            // profile refuses the VBR rung it names, and permits an equivalent label it does not,
            // because that label falls through to the codec group where another rung is allowed.
            var profile = WithRungs(("MP3 320kbps", true), ("MP3 VBR", false));

            Assert.Equal(QualityGateVerdict.Refused, QualityGate.Evaluate("MP3 VBR", profile));
            Assert.Equal(QualityGateVerdict.Allowed, QualityGate.Evaluate("MP3 V0", profile));
        }

        private static QualityProfile RefuseEverything() => WithRungs(
            ("AAC 320kbps", false),
            ("MP3 320kbps", false),
            ("FLAC", false),
            ("OPUS 128kbps", false));

        private static QualityProfile AllowEverything() => WithRungs(
            ("AAC 320kbps", true),
            ("MP3 320kbps", true),
            ("FLAC", true),
            ("OPUS 128kbps", true));

        [Fact]
        public void TheMpeg4ContainerLabels_AreGatedOnTheAacRungs()
        {
            // This test used to be called ALabelWithNoCodecGroup_IsNeverRefused and it pinned the
            // defect as intended: AAX, AAXC and MP4 reached QualityGate unplaceable, came back
            // NoOpinion, and were permitted by a profile that refuses every rung it carries. They
            // are all MPEG-4 containers holding AAC, and QualityMatcher now says so, so they are
            // gated on the AAC rungs exactly as M4B and M4A always were.
            //
            // AAX is the one that mattered. It is Audible's container and the scorer ranks it 95,
            // second only to FLAC, so a profile refusing everything grabbed it in preference to
            // almost anything else.
            foreach (var container in new[] { "AAX", "AAXC", "MP4", "M4B", "M4A" })
            {
                Assert.Equal(QualityGateVerdict.Refused, QualityGate.Evaluate(container, RefuseEverything()));
                Assert.Equal(QualityGateVerdict.Allowed, QualityGate.Evaluate(container, AllowEverything()));
            }

            // The control that has to come out differently: the same labels against a ladder with
            // no AAC rung at all. That is the second rule, not the third, and it stays silent.
            var noAacRung = WithRungs(("MP3 320kbps", false), ("FLAC", false));
            foreach (var container in new[] { "AAX", "AAXC", "MP4", "M4B" })
            {
                Assert.Equal(QualityGateVerdict.NoOpinion, QualityGate.Evaluate(container, noAacRung));
            }
        }

        [Fact]
        public void ALabelTheGateCannotPlace_IsRefused()
        {
            // The third rule. No rung names these and nothing can say what codec they are, so no
            // profile carrying a ladder permits them. Refusal here does not depend on the Allowed
            // flags, which is the point: the ladder is an allow-list and these are not on it.
            var unplaceable = new[] { "M4P", "WMA", "AC3", "EAC3", "DTS", "V0", "V2", "CBR", "Lossless" };

            foreach (var label in unplaceable)
            {
                Assert.Equal(QualityGateVerdict.Refused, QualityGate.Evaluate(label, RefuseEverything()));
                Assert.Equal(QualityGateVerdict.Refused, QualityGate.Evaluate(label, AllowEverything()));
            }

            // Three controls that must each come out differently, or the assertions above would
            // pass against a gate that simply refused everything.
            //
            // One: a profile with no ladder still gates nothing, so this is a property of the
            // ladder rather than of the label.
            foreach (var label in unplaceable)
            {
                Assert.Equal(QualityGateVerdict.NoOpinion, QualityGate.Evaluate(label, new QualityProfile()));
            }

            // Two: the same VBR preset spelled the way a parser actually emits it is placed by its
            // codec and permitted. "V0" alone is refused; "MP3 V0" is not.
            Assert.Equal(QualityGateVerdict.Allowed, QualityGate.Evaluate("MP3 V0", AllowEverything()));

            // Three: a bare bitrate has no codec group either, but the name rule places it before
            // the group is ever consulted, so it is decided by the rung that carries that bitrate.
            Assert.Equal(QualityGateVerdict.Refused, QualityGate.Evaluate("320kbps", RefuseEverything()));
            Assert.Equal(QualityGateVerdict.Allowed, QualityGate.Evaluate("320kbps", AllowEverything()));
        }

        /// <summary>
        /// The allow-list this gate replaced, transcribed from SearchResultScorer as it stood at
        /// a630572e9:287-300, minus the step that appended PreferredFormats. Dropping that step is
        /// the whole point of the change (a preference must not widen a gate), so the reference
        /// has to be the rule without it or the comparison below would only restate the change.
        ///
        /// One faithful-transcription caveat, deliberately not reproduced. QualityDefinition.Quality
        /// defaults to "", and the real allow-list put that empty string in its list, where
        /// label.Contains("") is true for every label. So a profile carrying an allowed rung with
        /// no name accepted everything. QualityGate drops blank-named rungs instead, which means it
        /// refuses unplaceable labels against such a profile where the old code accepted them. That
        /// is a divergence and it is intended: a rung with no name says nothing about what it
        /// covers, and matching everything was an accident of substring semantics rather than a
        /// behaviour worth keeping. See TheOldAllowListsBlankRungWildcard_IsNotReproduced.
        /// </summary>
        private static bool CanaryAcceptedOnItsRungs(string label, QualityProfile profile)
        {
            if (profile.Qualities == null || profile.Qualities.Count == 0)
            {
                return true;
            }

            var allowed = profile.Qualities
                .Where(rung => rung.Allowed)
                .Select(rung => (rung.Quality ?? string.Empty).ToLowerInvariant())
                .ToList();

            var detected = label.ToLowerInvariant();
            return allowed.Any(rung => detected.Contains(rung, StringComparison.Ordinal)
                                       || rung.Contains(detected, StringComparison.Ordinal));
        }

        [Fact]
        public void NothingTheOldAllowListPermitted_IsRefusedHere()
        {
            // The guard against over-correcting: wherever the old allow-list accepted on its own
            // rungs, this gate must not refuse.
            //
            // Be clear about what this can and cannot catch, because the obvious reading of it is
            // wrong. The old allow-list accepted only when an allowed rung name-matched the label,
            // so every pair it accepts here is decided by rule 1, the name match, which this commit
            // does not touch. By construction no accepted pair ever reaches the codec-group
            // fallback or the third rule, and mutating either of those does not fail this test.
            // What it pins is that rule 1 still decides every pair the old code decided, and that
            // the later rules never get a chance to overrule it.
            //
            // Over-correction in the other two rules is caught elsewhere, and was measured to be:
            // turning the rule-2 miss into a refusal fails SeededDefaultLadder_HasNoOpinion...,
            // ALabelNamingNoRung_FallsBackToItsCodecGroup, and both over-correction guards in
            // QualityAllowedGateTests. Rule 3 is pinned by ALabelTheGateCannotPlace_IsRefused.
            var labels = new[]
            {
                "AAX", "AAXC", "MP4", "M4P", "M4B", "M4A", "WMA", "AC3", "EAC3", "DTS",
                "V0", "V2", "CBR", "Lossless", "FLAC", "OPUS", "MP3 320kbps", "MP3 64kbps",
                "MP3 VBR", "MP3 V0", "AAC 256kbps", "320kbps"
            };

            var profiles = new[]
            {
                AllowEverything(),
                RefuseEverything(),
                WithRungs(("MP3 320kbps", true), ("MP3 64kbps", false)),
                WithRungs(("AAC 256kbps", true), ("AAC 64kbps", false), ("MP3 320kbps", false)),
                WithRungs(("FLAC", false)),
                new QualityProfile()
            };

            var accepted = 0;
            var acceptedWithALadder = 0;
            foreach (var profile in profiles)
            {
                var hasLadder = profile.Qualities != null && profile.Qualities.Count > 0;
                foreach (var label in labels)
                {
                    if (!CanaryAcceptedOnItsRungs(label, profile))
                    {
                        continue;
                    }

                    accepted++;
                    if (hasLadder)
                    {
                        acceptedWithALadder++;
                    }

                    Assert.NotEqual(QualityGateVerdict.Refused, QualityGate.Evaluate(label, profile));
                }
            }

            // The control against a vacuous pass, counted per profile rather than in total. Most
            // of the accepted pairs come from the profile with no ladder at all, where Evaluate
            // short-circuits before any rule runs, so a total over the whole matrix would be
            // satisfied by the degenerate row alone and would prove nothing about the rules.
            Assert.True(
                acceptedWithALadder >= 7,
                $"only {acceptedWithALadder} accepted pairs came from a profile that has a ladder");
            Assert.True(accepted > acceptedWithALadder, "the no-ladder row contributed nothing");
        }

        [Fact]
        public void TheOldAllowListsBlankRungWildcard_IsNotReproduced()
        {
            // The one place this gate refuses what the old allow-list accepted, pinned so it is a
            // decision on the record rather than something found later. QualityDefinition.Quality
            // defaults to "", the old list held that empty string, and Contains("") is true of
            // every label, so one allowed unnamed rung permitted everything.
            var blankRung = new QualityProfile
            {
                Qualities = new List<QualityDefinition>
                {
                    new() { Quality = string.Empty, Allowed = true, Priority = 0 },
                    new() { Quality = "MP3 320kbps", Allowed = false, Priority = 1 }
                }
            };

            Assert.True(CanaryAcceptedOnItsRungs("WMA", blankRung), "the reference accepted it");
            Assert.Equal(QualityGateVerdict.Refused, QualityGate.Evaluate("WMA", blankRung));

            // The control: the named rung in the same profile is still read normally, so the blank
            // one is being ignored rather than the whole profile being discarded.
            Assert.Equal(QualityGateVerdict.Refused, QualityGate.Evaluate("MP3 320kbps", blankRung));
            Assert.Equal(QualityGateVerdict.NoOpinion, QualityGate.Evaluate("FLAC", blankRung));
        }

        [Fact]
        public void SeededDefaultLadder_HasNoOpinionAboutTheCodecsItDoesNotList()
        {
            // The eleven rungs QualityProfileService seeds into the default profile, all allowed,
            // which is the shape a stock install actually has.
            var seeded = WithRungs(
                ("AAC 320kbps", true), ("AAC 256kbps", true), ("AAC 192kbps", true),
                ("AAC 128kbps", true), ("AAC 64kbps", true),
                ("MP3 320kbps", true), ("MP3 256kbps", true), ("MP3 VBR", true),
                ("MP3 192kbps", true), ("MP3 128kbps", true), ("MP3 64kbps", true));

            Assert.Equal(QualityGateVerdict.Allowed, QualityGate.Evaluate("MP3 320kbps", seeded));
            Assert.Equal(QualityGateVerdict.Allowed, QualityGate.Evaluate("M4B", seeded));
            Assert.Equal(QualityGateVerdict.NoOpinion, QualityGate.Evaluate("FLAC", seeded));
            Assert.Equal(QualityGateVerdict.NoOpinion, QualityGate.Evaluate("OPUS", seeded));
        }
    }
}
