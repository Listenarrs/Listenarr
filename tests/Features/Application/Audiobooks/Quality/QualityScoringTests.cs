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
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Quality
{
    public class QualityScoringTests
    {
        [Fact]
        public void GetQualityScore_VariousTokens_ReturnsExpected()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var svc = new QualityProfileService(new QualityProfileRepository(db), NullLogger<QualityProfileService>.Instance);

            var method = typeof(QualityProfileService).GetMethod("GetQualityScore", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            // MP3 VBR should be the mid-range score (65)
            var vbr = (int)method.Invoke(svc, new object[] { "MP3 VBR" });
            Assert.Equal(65, vbr);

            // V0/V1/V2 presets
            var v0 = (int)method.Invoke(svc, new object[] { "MP3 V0" });
            var v1 = (int)method.Invoke(svc, new object[] { "MP3 V1" });
            var v2 = (int)method.Invoke(svc, new object[] { "MP3 V2" });
            Assert.True(v0 > v1 && v1 > v2);

            // Numeric bitrates
            Assert.Equal(80, (int)method.Invoke(svc, new object[] { "MP3 320kbps" }));
            Assert.Equal(74, (int)method.Invoke(svc, new object[] { "MP3 256kbps" }));

            // Opus/AAC/AAX
            Assert.Equal(85, (int)method.Invoke(svc, new object[] { "Opus VBR" }));
            Assert.Equal(78, (int)method.Invoke(svc, new object[] { "AAC 256" }));
            Assert.Equal(95, (int)method.Invoke(svc, new object[] { "AAX" }));
        }

        [Fact]
        public async Task ScoreSearchResult_AppliesPenaltiesAndQualityScore()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var svc = new QualityProfileService(new QualityProfileRepository(db), NullLogger<QualityProfileService>.Instance);

            // Create a default profile since in-memory DB starts empty
            var profile = new QualityProfile { Name = "Default", IsDefault = true };
            db.QualityProfiles.Add(profile);
            await db.SaveChangesAsync();
            profile = await svc.GetDefaultAsync();

            var result = new SearchResult
            {
                Id = Guid.NewGuid().ToString(),
                Title = "JANE AUSTEN Pride And Prejudice [ Stevenson]",
                Quality = "MP3 VBR",
                Size = 809404474, // ~772 MB
                DownloadType = "DDL",
                Source = "Test (Internet Archive)",
                Format = null,
                Language = null
            };

            var score = await svc.ScoreSearchResult(result, profile);

            // Quality score for MP3 VBR is 65; language penalty -10 (profile prefers English); format penalty -8
            // Total expected = 65 - 10 - 8 = 47
            Assert.Equal(47, score.TotalScore);
            Assert.True(score.ScoreBreakdown.TryGetValue("Quality", out var qualityScore));
            Assert.Equal(65, qualityScore);
            Assert.Equal(-10, score.ScoreBreakdown["Language"]);
            Assert.Equal(-8, score.ScoreBreakdown["Format"]);

            // Smart composite should be present and contain expected breakdown keys
            Assert.True(score.SmartScore > 0);
            Assert.True(score.SmartScoreBreakdown.TryGetValue("Quality", out _));
            Assert.True(score.SmartScoreBreakdown.TryGetValue("Format", out _));
            Assert.True(score.SmartScoreBreakdown.TryGetValue("Seed", out _));
        }
    }
}

