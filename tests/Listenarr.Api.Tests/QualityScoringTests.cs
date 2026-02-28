using System;
using System.Reflection;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Api.Tests
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
            var svc = new QualityProfileService(db, NullLogger<QualityProfileService>.Instance);

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
            var svc = new QualityProfileService(db, NullLogger<QualityProfileService>.Instance);

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
            Assert.True(score.ScoreBreakdown.ContainsKey("Quality"));
            Assert.Equal(65, score.ScoreBreakdown["Quality"]);
            Assert.Equal(-10, score.ScoreBreakdown["Language"]);
            Assert.Equal(-8, score.ScoreBreakdown["Format"]);

            // Smart composite should be present and contain expected breakdown keys
            Assert.True(score.SmartScore > 0);
            Assert.True(score.SmartScoreBreakdown.ContainsKey("Quality"));
            Assert.True(score.SmartScoreBreakdown.ContainsKey("Format"));
            Assert.True(score.SmartScoreBreakdown.ContainsKey("Seed"));
        }
    }
}

