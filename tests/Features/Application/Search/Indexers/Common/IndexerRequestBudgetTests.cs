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

namespace Listenarr.Tests.Features.Application.Search.Indexers.Common
{
    // The per-indexer rolling daily request budget for #639: ceilings by origin, the rolling
    // window edge, atomic check-and-spend, persistence and the sweep-headroom check. The
    // clock and the store are fakes so every case is exact.
    [Trait("Name", "IndexerRequestBudgetTests")]
    [Trait("Category", "IndexerSearchWorkflow")]
    public sealed class IndexerRequestBudgetTests : BaseTests
    {
        private const int Reserve = 25;
        private static readonly DateTimeOffset Start = new(2026, 3, 1, 10, 59, 0, TimeSpan.Zero);

        [Fact]
        public async Task TryAcquireAsync_NoLimit_AdmitsEverythingAndRecordsNothing()
        {
            var store = new RecordingUsageStore();
            var budget = CreateBudget(store, new MutableClock(Start));
            var indexer = BudgetedIndexer(1, limit: null);

            for (var i = 0; i < 10_000; i++)
            {
                Assert.True(await budget.TryAcquireAsync(indexer, SearchOrigin.Sweep, Reserve));
            }

            Assert.Equal(0, store.SaveCalls);
        }

        [Theory]
        [InlineData(100, 25, 75)]
        [InlineData(2000, 25, 1500)]
        [InlineData(10, 25, 7)]
        [InlineData(100, 0, 100)]
        [InlineData(100, 95, 10)]
        public void CeilingFor_Sweep_IsLimitLessReserveRoundedDown(int limit, int reserve, int expected)
        {
            Assert.Equal(expected, IndexerRequestBudget.CeilingFor(limit, SearchOrigin.Sweep, reserve));
            Assert.Equal(limit, IndexerRequestBudget.CeilingFor(limit, SearchOrigin.Interactive, reserve));
        }

        [Fact]
        public async Task TryAcquireAsync_SweepStopsAtItsShare_InteractiveSpendsTheReserve()
        {
            var budget = CreateBudget(new RecordingUsageStore(), new MutableClock(Start));
            var indexer = BudgetedIndexer(1, limit: 100);

            var sweepAdmitted = await AcquireUntilRefusedAsync(budget, indexer, SearchOrigin.Sweep);
            var interactiveAdmitted = await AcquireUntilRefusedAsync(budget, indexer, SearchOrigin.Interactive);

            Assert.Equal(75, sweepAdmitted);
            Assert.Equal(25, interactiveAdmitted);
            Assert.False(await budget.TryAcquireAsync(indexer, SearchOrigin.Sweep, Reserve));
        }

        [Fact]
        public async Task TryAcquireAsync_RetryStopsWhereTheSweepDoes()
        {
            var budget = CreateBudget(new RecordingUsageStore(), new MutableClock(Start));
            var indexer = BudgetedIndexer(1, limit: 100);

            Assert.Equal(40, await AcquireUntilRefusedAsync(budget, indexer, SearchOrigin.Sweep, stopAfter: 40));
            Assert.Equal(35, await AcquireUntilRefusedAsync(budget, indexer, SearchOrigin.Retry));
            Assert.False(await budget.TryAcquireAsync(indexer, SearchOrigin.Sweep, Reserve));
            Assert.True(await budget.TryAcquireAsync(indexer, SearchOrigin.Interactive, Reserve));
        }

        [Fact]
        public async Task TryAcquireAsync_IndexersHaveSeparateBudgets()
        {
            var budget = CreateBudget(new RecordingUsageStore(), new MutableClock(Start));
            var first = BudgetedIndexer(1, limit: 4);
            var second = BudgetedIndexer(2, limit: 4);

            Assert.Equal(4, await AcquireUntilRefusedAsync(budget, first, SearchOrigin.Interactive));
            Assert.Equal(4, await AcquireUntilRefusedAsync(budget, second, SearchOrigin.Interactive));
        }

        [Fact]
        public async Task TryAcquireAsync_RequestStillCountsAt24Hours_AndDropsOutWithItsHour()
        {
            // Spent at 10:59; the 10:00 bucket counts until the current hour is 11:00 the next
            // day, so a full 24 hours after the spend the indexer is still refused.
            var clock = new MutableClock(Start);
            var budget = CreateBudget(new RecordingUsageStore(), clock);
            var indexer = BudgetedIndexer(1, limit: 4);
            Assert.Equal(4, await AcquireUntilRefusedAsync(budget, indexer, SearchOrigin.Interactive));

            clock.Now = Start.AddHours(24);
            Assert.False(await budget.TryAcquireAsync(indexer, SearchOrigin.Interactive, Reserve));

            clock.Now = new DateTimeOffset(2026, 3, 2, 11, 0, 0, TimeSpan.Zero);
            Assert.True(await budget.TryAcquireAsync(indexer, SearchOrigin.Interactive, Reserve));
        }

        [Fact]
        public async Task TryAcquireAsync_NeverUnderCountsAgainstATrue24HourWindow()
        {
            // One request per simulated 7 minutes for two days against a generous limit; at
            // every step the budget's count must be at least the number of requests sent in the
            // preceding 24 hours.
            var clock = new MutableClock(Start);
            var budget = CreateBudget(new RecordingUsageStore(), clock);
            var sent = new List<DateTimeOffset>();
            var indexer = BudgetedIndexer(1, limit: 1_000_000);

            for (var minute = 0; minute < 48 * 60; minute += 7)
            {
                clock.Now = Start.AddMinutes(minute);
                Assert.True(await budget.TryAcquireAsync(indexer, SearchOrigin.Interactive, Reserve));
                sent.Add(clock.Now);

                var trueCount = sent.Count(at => at > clock.Now.AddHours(-24));
                var probe = BudgetedIndexer(1, limit: trueCount);
                Assert.False(
                    await budget.TryAcquireAsync(probe, SearchOrigin.Interactive, Reserve),
                    $"under-counted at {clock.Now:u}: {trueCount} requests in the last 24 hours");
            }
        }

        [Fact]
        public async Task TryAcquireAsync_ConcurrentCallers_AdmitExactlyTheLimit()
        {
            var store = new RecordingUsageStore();
            var budget = CreateBudget(store, new MutableClock(Start));
            var indexer = BudgetedIndexer(1, limit: 50);
            using var gate = new ManualResetEventSlim(false);

            var attempts = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
            {
                gate.Wait();
                var origin = i % 2 == 0 ? SearchOrigin.Interactive : SearchOrigin.Sweep;
                return await budget.TryAcquireAsync(indexer, origin, Reserve);
            })).ToList();
            gate.Set();
            var admitted = (await Task.WhenAll(attempts)).Count(result => result);

            Assert.Equal(50, admitted);
            Assert.Equal(50, store.Rows.Single().Count);
        }

        [Fact]
        public async Task TryAcquireAsync_AfterRestart_SeesTheUsageAlreadySpent()
        {
            var store = new RecordingUsageStore();
            var clock = new MutableClock(Start);
            var indexer = BudgetedIndexer(1, limit: 100);
            var beforeRestart = CreateBudget(store, clock);
            for (var i = 0; i < 60; i++)
            {
                Assert.True(await beforeRestart.TryAcquireAsync(indexer, SearchOrigin.Sweep, Reserve));
            }

            clock.Now = Start.AddMinutes(30);
            var afterRestart = CreateBudget(store, clock);

            Assert.Equal(15, await AcquireUntilRefusedAsync(afterRestart, indexer, SearchOrigin.Sweep));
        }

        [Fact]
        public async Task TryAcquireAsync_StoreFailure_StillAdmitsAndKeepsCounting()
        {
            var store = new RecordingUsageStore { FailWrites = true };
            var budget = CreateBudget(store, new MutableClock(Start));
            var indexer = BudgetedIndexer(1, limit: 3);

            Assert.Equal(3, await AcquireUntilRefusedAsync(budget, indexer, SearchOrigin.Interactive));
            Assert.Equal(3, store.SaveCalls);
        }

        [Fact]
        public async Task TryAcquireAsync_LoadFailure_StillEnforcesTheLimitAndLoadsOnlyOnce()
        {
            var store = new RecordingUsageStore { FailLoads = true };
            var budget = CreateBudget(store, new MutableClock(Start));
            var indexer = BudgetedIndexer(1, limit: 3);

            Assert.Equal(3, await AcquireUntilRefusedAsync(budget, indexer, SearchOrigin.Interactive));
            Assert.Equal(1, store.LoadCalls);
        }

        [Fact]
        public async Task TryAcquireAsync_NewHour_PrunesRowsOutsideTheWindow()
        {
            var store = new RecordingUsageStore();
            var clock = new MutableClock(Start);
            var budget = CreateBudget(store, clock);
            var indexer = BudgetedIndexer(1, limit: 100);
            Assert.True(await budget.TryAcquireAsync(indexer, SearchOrigin.Sweep, Reserve));

            clock.Now = Start.AddHours(26);
            Assert.True(await budget.TryAcquireAsync(indexer, SearchOrigin.Sweep, Reserve));

            var row = Assert.Single(store.Rows);
            Assert.Equal(new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc), row.HourStartUtc);
        }

        [Fact]
        public async Task HasSweepHeadroomAsync_FalseOnlyWhenEveryIndexerIsBudgetedAndSpent()
        {
            var clock = new MutableClock(Start);
            var budget = CreateBudget(new RecordingUsageStore(), clock);
            var spent = BudgetedIndexer(1, limit: 4);
            var fresh = BudgetedIndexer(2, limit: 4);
            var unlimited = BudgetedIndexer(3, limit: null);
            Assert.Equal(3, await AcquireUntilRefusedAsync(budget, spent, SearchOrigin.Sweep));

            Assert.True(await budget.HasSweepHeadroomAsync(Array.Empty<Indexer>(), Reserve));
            Assert.True(await budget.HasSweepHeadroomAsync(new[] { spent, fresh }, Reserve));
            Assert.True(await budget.HasSweepHeadroomAsync(new[] { spent, unlimited }, Reserve));
            Assert.False(await budget.HasSweepHeadroomAsync(new[] { spent }, Reserve));

            clock.Now = Start.AddHours(25);
            Assert.True(await budget.HasSweepHeadroomAsync(new[] { spent }, Reserve));
        }

        private static IndexerRequestBudget CreateBudget(IIndexerRequestUsageStore store, TimeProvider clock) =>
            new(store, clock, NullLogger<IndexerRequestBudget>.Instance);

        private static Indexer BudgetedIndexer(int id, int? limit) =>
            new() { Id = id, Name = $"Indexer {id}", DailyRequestLimit = limit };

        private static async Task<int> AcquireUntilRefusedAsync(IIndexerRequestBudget budget, Indexer indexer, SearchOrigin origin, int stopAfter = int.MaxValue)
        {
            var admitted = 0;
            while (admitted < stopAfter && await budget.TryAcquireAsync(indexer, origin, Reserve))
            {
                admitted++;
                Assert.True(admitted <= 1_000_000, "budget never refused");
            }
            return admitted;
        }

        private sealed class MutableClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed class RecordingUsageStore : IIndexerRequestUsageStore
        {
            private readonly object _gate = new();
            private readonly Dictionary<(int, DateTime), int> _rows = new();

            public bool FailWrites { get; init; }

            public bool FailLoads { get; init; }

            public int LoadCalls { get; private set; }

            public int SaveCalls { get; private set; }

            public List<IndexerRequestUsage> Rows
            {
                get
                {
                    lock (_gate)
                    {
                        return _rows.Select(row => new IndexerRequestUsage
                        {
                            IndexerId = row.Key.Item1,
                            HourStartUtc = row.Key.Item2,
                            Count = row.Value
                        }).ToList();
                    }
                }
            }

            public Task<IReadOnlyList<IndexerRequestUsage>> LoadSinceAsync(DateTime sinceHourUtc, CancellationToken ct = default)
            {
                LoadCalls++;
                if (FailLoads)
                    throw new InvalidOperationException("simulated load failure");
                return Task.FromResult<IReadOnlyList<IndexerRequestUsage>>(
                    Rows.Where(row => row.HourStartUtc >= sinceHourUtc).ToList());
            }

            public Task SaveAsync(int indexerId, DateTime hourStartUtc, int count, CancellationToken ct = default)
            {
                lock (_gate)
                {
                    SaveCalls++;
                    if (FailWrites)
                        throw new InvalidOperationException("simulated store failure");
                    _rows[(indexerId, hourStartUtc)] = count;
                }
                return Task.CompletedTask;
            }

            public Task DeleteBeforeAsync(DateTime beforeHourUtc, CancellationToken ct = default)
            {
                lock (_gate)
                {
                    foreach (var key in _rows.Keys.Where(key => key.Item2 < beforeHourUtc).ToList())
                    {
                        _rows.Remove(key);
                    }
                }
                return Task.CompletedTask;
            }
        }
    }
}
