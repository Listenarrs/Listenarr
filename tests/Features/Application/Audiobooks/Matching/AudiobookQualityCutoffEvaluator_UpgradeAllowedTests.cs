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

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Matching
{
    /// <summary>
    /// AudiobookQualityCutoffEvaluator is deliberately not changed by the UpgradeAllowed flag, and
    /// these pin that. Its guard on a blank cutoff is the subject of separate work, so this branch
    /// has to leave the answer for a blank-cutoff profile exactly where it found it, and only then
    /// make sure the state the flag newly allows, upgrades off with a cutoff still named, is
    /// answered through QualityMatcher rather than by comparing against a cutoff nobody is
    /// upgrading towards.
    /// </summary>
    [Trait("Name", nameof(AudiobookQualityCutoffEvaluator_UpgradeAllowedTests))]
    [Trait("Category", "Application")]
    public class AudiobookQualityCutoffEvaluator_UpgradeAllowedTests : BaseTests
    {
        private static QualityProfileBuilder StructuredProfile() =>
            new QualityProfileBuilder().WithName("Structured").WithStructuredDefaults();

        /// <summary>
        /// A profile that recorded upgrades-off the old way, by blanking the cutoff, gets the same
        /// answer it got before this branch. The migration leaves exactly this shape behind, so
        /// this is the case that must not move.
        /// </summary>
        [Fact]
        public async Task BlankCutoff_WithUpgradesOff_AnswersTheSameAsABlankCutoffAlone()
        {
            var withFlag = await EvaluateAsync(
                StructuredProfile().WithCutoff("").WithUpgradesDisabled().Build());
            var withoutFlag = await EvaluateAsync(StructuredProfile().WithCutoff("").Build());

            // Named, not merely compared. Asserting only that the two agree would still pass if
            // the blank-cutoff answer itself moved, which is exactly what this is meant to catch.
            Assert.False(withoutFlag);
            Assert.Equal(withoutFlag, withFlag);
        }

        /// <summary>
        /// The state only this branch can produce. The profile names a cutoff the file is well
        /// below, but upgrades are off, so there is nothing to search for and the cutoff counts as
        /// met.
        /// </summary>
        [Fact]
        public async Task NamedCutoff_WithUpgradesOff_IsMet_EvenThoughTheFileIsBelowIt()
        {
            var met = await EvaluateAsync(
                StructuredProfile().WithCutoff("AAC 320kbps").WithUpgradesDisabled().Build());

            Assert.True(met);
        }

        /// <summary>
        /// The control. The identical profile and file with upgrades on is still short of the
        /// cutoff, so the answer above is the flag and not the file.
        /// </summary>
        [Fact]
        public async Task NamedCutoff_WithUpgradesOn_IsNotMet_ForTheSameFile()
        {
            var profile = StructuredProfile().WithCutoff("AAC 320kbps").Build();

            Assert.True(profile.UpgradeAllowed);
            Assert.False(await EvaluateAsync(profile));
        }

        /// <summary>
        /// A second control, so the harness is not simply answering "met" for everything: a file
        /// at or above the named cutoff is met with upgrades on too.
        /// </summary>
        [Fact]
        public async Task NamedCutoff_WithUpgradesOn_IsMet_WhenTheFileReachesIt()
        {
            var profile = StructuredProfile().WithCutoff("AAC 64kbps").Build();

            Assert.True(await EvaluateAsync(profile));
        }

        /// <summary>
        /// Runs the evaluator over one stored 128kbps AAC file, which sits below AAC 320kbps and
        /// at or above AAC 64kbps on the structured ladder.
        /// </summary>
        private static async Task<bool> EvaluateAsync(QualityProfile profile)
        {
            const int audiobookId = 4242;
            var audiobook = new Audiobook { Id = audiobookId, QualityProfile = profile };

            var file = AudiobookFile.CreateUnresolved($"/library/book-{audiobookId}.m4b");
            file.AudiobookId = audiobookId;
            file.Codec = "aac";
            file.Bitrate = 128_000;

            var downloadRepository = new Mock<IDownloadRepository>(MockBehavior.Strict);
            downloadRepository
                .Setup(repository => repository.GetByAudiobookIdAsync(
                    audiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Download>());

            var fileRepository = new Mock<IAudiobookFileRepository>(MockBehavior.Strict);
            fileRepository
                .Setup(repository => repository.GetByAudiobookIdAsync(
                    audiobookId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<AudiobookFile> { file });

            return await AudiobookQualityCutoffEvaluator.IsQualityCutoffMetAsync(
                audiobook,
                downloadRepository.Object,
                fileRepository.Object);
        }
    }
}
