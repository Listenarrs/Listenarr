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

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_QualityCutoffTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_QualityCutoffTests : BaseTests
    {
        [Fact]
        [Trait("Method", "IsQualityCutoffMetAsync")]
        [Trait("Scenario", "ImportPendingDownload_ReturnsTrue")]
        public async Task IsQualityCutoffMetAsync_ImportPendingDownload_ReturnsTrue()
        {
            // Given
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Dune")
                .WithQualityProfile(new QualityProfile
                {
                    CutoffQuality = "MP3",
                    Qualities = new List<QualityDefinition>
                    {
                        new() { Quality = "MP3", Priority = 1 }
                    }
                })
                .Build());

            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("dl-1")
                .WithAudiobookId(audiobook.Id)
                .WithStatus(DownloadStatus.ImportPending)
                .WithTitle("Dune")
                .Build());

            // When
            var result = await AudiobookQualityCutoffEvaluator.IsQualityCutoffMetAsync(
                audiobook,
                _downloadRepository,
                _audiobookFileRepository);

            // Then
            Assert.True(result);
        }

        [Fact]
        [Trait("Method", "IsQualityCutoffMetAsync")]
        [Trait("Scenario", "DisabledQualityRung_ReturnsFalse")]
        public async Task IsQualityCutoffMetAsync_DisabledMatchingQualityRung_ReturnsFalse()
        {
            // Given
            var profile = new QualityProfile
            {
                Name = "Disabled AAC cutoff",
                CutoffQuality = "AAC 256kbps",
                Qualities = new List<QualityDefinition>
                {
                    new()
                    {
                        Quality = "AAC 256kbps",
                        Codec = "AAC",
                        Bitrate = 256,
                        Priority = 0,
                        Allowed = false
                    }
                }
            };

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Disabled AAC")
                .WithQualityProfile(profile)
                .Build());

            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(@"C:\books\disabled-aac\book.m4b")
                .WithFormat("m4b")
                .WithCoded("aac")
                .WithBitrate(256_000)
                .WithSize(123_456)
                .Build());

            // When
            var result = await AudiobookQualityCutoffEvaluator.IsQualityCutoffMetAsync(
                audiobook,
                _downloadRepository,
                _audiobookFileRepository);

            // Then
            Assert.False(result);
        }
    }
}
