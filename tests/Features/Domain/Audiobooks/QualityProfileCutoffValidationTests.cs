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
using System.ComponentModel.DataAnnotations;
using DataAnnotationsValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Audiobooks
{
    /// <summary>
    /// The cutoff rule mirrors Readarr and Sonarr, which both express it as
    /// <c>return cutoffItem is { Allowed: true };</c> against the profile's own items
    /// (src/Readarr.Api.V1/Profiles/Quality/QualityCutoffValidator.cs:26-28,
    /// src/Sonarr.Api.V3/Profiles/Quality/QualityCutoffValidator.cs:26-28).
    ///
    /// These exercise the rule the way ASP.NET Core does during model binding: DataAnnotations
    /// over the whole object.
    /// </summary>
    [Trait("Name", "QualityProfileCutoffValidationTests")]
    [Trait("Category", "Domain")]
    public sealed class QualityProfileCutoffValidationTests : BaseTests
    {
        [Fact]
        public void ValidCutoff_NamingAnAllowedQuality_IsAccepted()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Accepts a good cutoff")
                .WithStructuredDefaults()
                .WithCutoff("AAC 128kbps")
                .Build();

            var results = Validate(profile);

            Assert.Empty(results);
        }

        [Fact]
        public void ValidCutoff_NamingTheOnlyAllowedQuality_IsAcceptedAmongDisallowedRungs()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Accepts the one allowed rung")
                .WithQuality("FLAC", 0, codec: "FLAC", lossless: true, allowed: false)
                .WithQuality("AAC 320kbps", 1, codec: "AAC", bitrate: 320, allowed: true)
                .WithQuality("MP3 128kbps", 2, codec: "MP3", bitrate: 128, allowed: false)
                .WithCutoff("AAC 320kbps")
                .Build();

            var results = Validate(profile);

            Assert.Empty(results);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankCutoff_IsRefused(string? cutoff)
        {
            var profile = new QualityProfileBuilder()
                .WithName("Blank cutoff")
                .WithStructuredDefaults()
                .Build();
            profile.CutoffQuality = cutoff;

            AssertRefusedForCutoff(profile);
        }

        [Fact]
        public void CutoffNamingAQualityTheProfileDoesNotCarry_IsRefused()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Cutoff off the ladder")
                .WithStructuredDefaults()
                .WithCutoff("OPUS 96kbps")
                .Build();

            Assert.DoesNotContain(profile.Qualities, quality => quality.Quality == "OPUS 96kbps");
            AssertRefusedForCutoff(profile);
        }

        [Fact]
        public void CutoffNamingAPresentButDisallowedQuality_IsRefused()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Cutoff on a disabled rung")
                .WithStructuredDefaults()
                .WithQuality("OPUS 96kbps", 11, codec: "OPUS", bitrate: 96, allowed: false)
                .WithCutoff("OPUS 96kbps")
                .Build();

            Assert.Contains(profile.Qualities, quality => quality.Quality == "OPUS 96kbps" && !quality.Allowed);
            AssertRefusedForCutoff(profile);
        }

        /// <summary>
        /// The validator has to agree with the code that later resolves the stored value.
        /// QualityMatcher.FindAllowedRung (listenarr.domain/Common/QualityMatcher.cs:295-297)
        /// matches OrdinalIgnoreCase, so refusing a case-mismatched cutoff here would stop a
        /// profile saving that the matcher resolves perfectly well.
        /// </summary>
        [Fact]
        public void CutoffDifferingOnlyInCase_IsAccepted_AsTheMatcherResolvesIt()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Cutoff case")
                .WithStructuredDefaults()
                .WithCutoff("aac 128kbps")
                .Build();

            Assert.DoesNotContain(profile.Qualities, quality => quality.Quality == "aac 128kbps");
            AssertEngineResolvesTheCutoff(profile);
            Assert.Empty(Validate(profile));
        }

        [Fact]
        public void CutoffDifferingOnlyInCase_OnADisallowedRung_IsStillRefused()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Cutoff case on a disabled rung")
                .WithStructuredDefaults()
                .WithQuality("OPUS 96kbps", 11, codec: "OPUS", bitrate: 96, allowed: false)
                .WithCutoff("opus 96KBPS")
                .Build();

            AssertRefusedForCutoff(profile);
        }

        [Fact]
        public void BlankQualityNames_AreNeverMatchedByABlankCutoff()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Blank rung name")
                .WithQuality("   ", 0, allowed: true)
                .WithQuality("AAC 320kbps", 1, codec: "AAC", bitrate: 320, allowed: true)
                .WithCutoff("   ")
                .Build();

            AssertRefusedForCutoff(profile);
        }

        [Fact]
        public void DuplicateRungsOfTheSameAllowedState_FollowThatState()
        {
            var duplicatedAllowed = new QualityProfileBuilder()
                .WithName("Duplicate allowed rung")
                .WithQuality("AAC 320kbps", 0, codec: "AAC", bitrate: 320, allowed: true)
                .WithQuality("AAC 320kbps", 1, codec: "AAC", bitrate: 320, allowed: true)
                .WithCutoff("AAC 320kbps")
                .Build();

            var duplicatedDisallowed = new QualityProfileBuilder()
                .WithName("Duplicate disallowed rung")
                .WithQuality("AAC 320kbps", 0, codec: "AAC", bitrate: 320, allowed: false)
                .WithQuality("AAC 320kbps", 1, codec: "AAC", bitrate: 320, allowed: false)
                .WithCutoff("AAC 320kbps")
                .Build();

            Assert.Empty(Validate(duplicatedAllowed));
            AssertRefusedForCutoff(duplicatedDisallowed);
        }

        /// <summary>
        /// The mixed pair is the case that separates the engine's predicate from a
        /// match-by-name-then-check-Allowed one. Matching by name first picks whichever duplicate
        /// comes earlier in the list, so a disallowed rung sitting ahead of an allowed one would be
        /// refused here while QualityMatcher.FindAllowedRung
        /// (listenarr.domain/Common/QualityMatcher.cs:295-297) resolves it. Both orderings have to
        /// be accepted, or the validator is order-dependent and the engine is not.
        /// </summary>
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void DuplicateRungs_WithOnlyOneAllowed_AreAccepted_InEitherOrder(
            bool firstAllowed,
            bool secondAllowed)
        {
            var profile = new QualityProfileBuilder()
                .WithName("Mixed duplicate rung")
                .WithQuality("AAC 320kbps", 0, codec: "AAC", bitrate: 320, allowed: firstAllowed)
                .WithQuality("AAC 320kbps", 1, codec: "AAC", bitrate: 320, allowed: secondAllowed)
                .WithCutoff("AAC 320kbps")
                .Build();

            AssertEngineResolvesTheCutoff(profile);
            Assert.Empty(Validate(profile));
        }

        /// <summary>
        /// The rule and the disabled-upgrades checkbox have to be able to coexist, because every
        /// profile saved before UpgradeAllowed existed recorded "upgrades off" as a blank cutoff
        /// and has to keep saving. Readarr and Sonarr never had to make this exemption: their
        /// cutoff is a quality id that always resolves
        /// (src/Readarr.Api.V1/Profiles/Quality/QualityProfileController.cs:25).
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankCutoff_WithUpgradesOff_IsAccepted(string? cutoff)
        {
            var profile = new QualityProfileBuilder()
                .WithName("Upgrades off, blank cutoff")
                .WithStructuredDefaults()
                .WithUpgradesDisabled()
                .Build();
            profile.CutoffQuality = cutoff;

            Assert.Empty(Validate(profile));
        }

        /// <summary>
        /// The control. The same three cutoffs the rule refuses are still refused the moment
        /// upgrades are on, so turning the flag off is what excuses them and not some weakening of
        /// the rule itself.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("OPUS 96kbps")]
        public void TheSameCutoffs_AreStillRefused_WhenUpgradesAreOn(string? cutoff)
        {
            var profile = new QualityProfileBuilder()
                .WithName("Upgrades on")
                .WithStructuredDefaults()
                .Build();
            profile.CutoffQuality = cutoff;

            Assert.True(profile.UpgradeAllowed);
            AssertRefusedForCutoff(profile);
        }

        /// <summary>
        /// Upgrades off no longer forces the cutoff to be thrown away, so a disallowed one can be
        /// saved alongside it. That is deliberate: the user turned upgrades off, the cutoff is
        /// inert, and blanking it on their behalf is what made the modal's checkbox destructive in
        /// the first place.
        /// </summary>
        [Fact]
        public void UpgradesOff_AlsoExcusesACutoffOnADisallowedRung()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Upgrades off, disallowed cutoff")
                .WithStructuredDefaults()
                .WithQuality("OPUS 96kbps", 11, codec: "OPUS", bitrate: 96, allowed: false)
                .WithUpgradesDisabled()
                .WithCutoff("OPUS 96kbps")
                .Build();

            Assert.Empty(Validate(profile));
        }

        /// <summary>
        /// Turning upgrades off must not swallow the profile's other failures; the exemption is
        /// scoped to the cutoff.
        /// </summary>
        [Fact]
        public void UpgradesOff_DoesNotSuppressTheOtherProfileRules()
        {
            var profile = new QualityProfileBuilder()
                .WithStructuredDefaults()
                .WithUpgradesDisabled()
                .Build();
            profile.Name = string.Empty;
            profile.CutoffQuality = null;

            var results = Validate(profile);

            Assert.DoesNotContain(
                results,
                result => result.MemberNames.Contains(nameof(QualityProfile.CutoffQuality)));
            Assert.Contains(
                results,
                result => result.MemberNames.Contains(nameof(QualityProfile.Name)));
        }

        [Fact]
        public void CutoffFailure_DoesNotSuppressTheOtherProfileRules()
        {
            var profile = new QualityProfileBuilder()
                .WithStructuredDefaults()
                .Build();
            profile.Name = string.Empty;
            profile.CutoffQuality = null;

            var results = Validate(profile);

            Assert.Contains(results, result => result.MemberNames.Contains(nameof(QualityProfile.CutoffQuality)));
            Assert.Contains(results, result => result.MemberNames.Contains(nameof(QualityProfile.Name)));
        }

        /// <summary>
        /// QualityMatcher.LabelMeetsCutoff runs the stored cutoff through ResolveCutoff and so
        /// FindAllowedRung (listenarr.domain/Common/QualityMatcher.cs:274-284,295-297). Asking it
        /// whether the cutoff label itself meets the cutoff is therefore exactly "can the engine
        /// resolve this cutoff to an allowed rung", which is the question the validator has to give
        /// the same answer to. Meaningless for a blank cutoff, which short-circuits to true, so
        /// only the non-blank acceptance cases use it.
        /// </summary>
        private static void AssertEngineResolvesTheCutoff(QualityProfile profile)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.CutoffQuality));
            Assert.True(
                QualityMatcher.LabelMeetsCutoff(profile.CutoffQuality, profile),
                $"The engine could not resolve cutoff '{profile.CutoffQuality}', so this profile "
                + "is not a case where the validator and the engine ought to agree on acceptance.");
        }

        private static void AssertRefusedForCutoff(QualityProfile profile)
        {
            var results = Validate(profile);

            var cutoffFailure = Assert.Single(
                results,
                result => result.MemberNames.Contains(nameof(QualityProfile.CutoffQuality)));
            Assert.Equal(ValidCutoffAttribute.CutoffMessage, cutoffFailure.ErrorMessage);
        }

        private static List<DataAnnotationsValidationResult> Validate(QualityProfile profile)
        {
            var results = new List<DataAnnotationsValidationResult>();
            Validator.TryValidateObject(
                profile,
                new ValidationContext(profile),
                results,
                validateAllProperties: true);
            return results;
        }
    }
}
