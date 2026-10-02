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
    [Trait("Name", "QualityProfileScoringConcurrencyTests")]
    [Trait("Category", "Unit")]
    public class QualityProfileScoringConcurrencyTests : BaseTests
    {
        // IIndexerRepository is registered scoped and the ListenArrDbContext behind it is scoped
        // too, so every repository in a scope shares one context. EF rejects a second operation
        // started on a context while another is in flight. Asserting on the real exception would
        // mean racing it, so this counts overlap directly: the stub records the highest number of
        // calls it ever had in flight at once. Anything above one is the condition EF refuses.
        private sealed class OverlapRecordingIndexerRepository(int retention = 1500) : IIndexerRepository
        {
            private int _inFlight;
            public int MaxConcurrent { get; private set; }
            public int CallCount { get; private set; }

            public async Task<Indexer?> GetByIdAsync(int id, CancellationToken ct = default)
            {
                var now = Interlocked.Increment(ref _inFlight);
                lock (this)
                {
                    CallCount++;
                    if (now > MaxConcurrent) MaxConcurrent = now;
                }

                // A real query is not instantaneous. Without this the tasks can complete one at a
                // time by luck and the overlap the test exists to catch would go unobserved.
                await Task.Delay(20);

                Interlocked.Decrement(ref _inFlight);
                return new Indexer { Id = id, Name = $"indexer-{id}", Type = "Usenet", Retention = retention };
            }

            public Task<List<Indexer>> GetAllAsync(CancellationToken ct = default) =>
                Task.FromResult(new List<Indexer>());
            public Task<List<Indexer>> GetEnabledAsync(bool isAutomaticSearch, CancellationToken ct = default) =>
                Task.FromResult(new List<Indexer>());
            public Task<Indexer?> GetByNameAsync(string name, CancellationToken ct = default) =>
                Task.FromResult<Indexer?>(null);
            public Task<Indexer> AddAsync(Indexer indexer, CancellationToken ct = default) =>
                Task.FromResult(indexer);
            public Task UpdateAsync(Indexer indexer, CancellationToken ct = default) => Task.CompletedTask;
            public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        }

        [Fact]
        public async Task ScoreSearchResults_DoesNotQueryTheIndexerRepositoryConcurrently()
        {
            var indexerRepository = new OverlapRecordingIndexerRepository();
            var service = new QualityProfileService(
                Mock.Of<IQualityProfileRepository>(),
                NullLogger<QualityProfileService>.Instance,
                indexerRepository);

            // Twelve results across three indexers: enough to overlap, and enough to show that the
            // batch does not need one query per result.
            var searchResults = Enumerable.Range(0, 12)
                .Select(i => new SearchResult
                {
                    Id = $"result-{i}",
                    Title = $"A Book {i}",
                    IndexerId = (i % 3) + 1,
                    Format = "mp3",
                    Language = "English",
                    PublishedDate = DateTime.UtcNow.AddDays(-1).ToString("o")
                })
                .ToList();

            var profile = new QualityProfile
            {
                MinimumSize = 0,
                MaximumSize = 0,
                PreferredFormats = ["mp3"],
                PreferredWords = [],
                MustNotContain = [],
                MustContain = [],
                PreferredLanguages = ["English"],
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var scores = await service.ScoreSearchResults(searchResults, profile);

            Assert.Equal(searchResults.Count, scores.Count);
            Assert.Equal(1, indexerRepository.MaxConcurrent);
            // Three distinct indexers, so three lookups rather than one per result.
            Assert.Equal(3, indexerRepository.CallCount);
        }

        // The overlap assertion above passes whether or not the resolved indexers reach the
        // scorer: a batch that looks each indexer up once and then throws the answers away makes
        // exactly the same three non-overlapping calls. This asks the other half of the question.
        // Retention is 10 days and the result is 100 days old, so the indexer's own retention has
        // to be the thing that rejects it. A profile MaximumAge well beyond 100 days means nothing
        // else in the scorer can produce that rejection, and an empty or ignored dictionary leaves
        // retention at 0 and the result accepted.
        [Fact]
        public async Task ScoreSearchResults_AppliesTheResolvedIndexerRetention()
        {
            var indexerRepository = new OverlapRecordingIndexerRepository(retention: 10);
            var service = new QualityProfileService(
                Mock.Of<IQualityProfileRepository>(),
                NullLogger<QualityProfileService>.Instance,
                indexerRepository);

            var searchResults = new List<SearchResult>
            {
                new()
                {
                    Id = "aged-result",
                    Title = "A Book",
                    IndexerId = 7,
                    Format = "mp3",
                    Language = "English",
                    PublishedDate = DateTime.UtcNow.AddDays(-100).ToString("o")
                }
            };

            var profile = new QualityProfile
            {
                MinimumSize = 0,
                MaximumSize = 0,
                PreferredFormats = ["mp3"],
                PreferredWords = [],
                MustNotContain = [],
                MustContain = [],
                PreferredLanguages = ["English"],
                MinimumSeeders = 0,
                MaximumAge = 3650
            };

            var scores = await service.ScoreSearchResults(searchResults, profile);

            var score = Assert.Single(scores);
            Assert.True(score.IsRejected);
            Assert.Contains(score.RejectionReasons, reason => reason.Contains("indexer retention 10 days"));
        }
    }
}
