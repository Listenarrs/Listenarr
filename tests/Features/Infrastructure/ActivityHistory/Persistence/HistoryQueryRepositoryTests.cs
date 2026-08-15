/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.ActivityHistory.Persistence
{
    public sealed class HistoryQueryRepositoryTests : IDisposable
    {
        private readonly ListenArrDbContext _db;
        private readonly EfHistoryRepository _repository;

        public HistoryQueryRepositoryTests()
        {
            _db = new ListenArrDbContext(new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
            _repository = new EfHistoryRepository(_db);
        }

        [Fact]
        public async Task QueryAsync_FiltersSortsAndPagesCanonicalHistory()
        {
            _db.History.AddRange(
                Entry("corr-1", HistoryEvents.ImportStarted, HistoryOutcome.Requested, DateTime.UtcNow.AddMinutes(-3)),
                Entry("corr-1", HistoryEvents.ImportRetry, HistoryOutcome.Retrying, DateTime.UtcNow.AddMinutes(-2)),
                Entry("corr-1", HistoryEvents.Imported, HistoryOutcome.Succeeded, DateTime.UtcNow.AddMinutes(-1)),
                Entry("corr-2", HistoryEvents.Imported, HistoryOutcome.Succeeded, DateTime.UtcNow));
            await _db.SaveChangesAsync();

            var page = await _repository.QueryAsync(new HistoryQuery
            {
                CorrelationId = "corr-1",
                SortDirection = "asc",
                Limit = 2
            });

            Assert.Equal(3, page.Total);
            Assert.Equal(2, page.Records.Count);
            Assert.Equal(HistoryEvents.ImportStarted, page.Records[0].EventType);
            Assert.Equal(HistoryEvents.ImportRetry, page.Records[1].EventType);
        }

        [Fact]
        public async Task DeleteAllAsync_RemovesAllHistoryIncludingFormerMoveScanRequests()
        {
            _db.History.AddRange(
                new History
                {
                    CorrelationId = "move:pending",
                    EventType = HistoryEvents.ScanQueued,
                    Outcome = HistoryOutcome.Requested,
                    Source = "Move"
                },
                Entry("ordinary", HistoryEvents.Imported, HistoryOutcome.Succeeded, DateTime.UtcNow));
            await _db.SaveChangesAsync();

            await _repository.DeleteAllAsync();

            Assert.Empty(await _db.History.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task DeleteAsync_FormerMoveScanRequest_IsOrdinaryHistory()
        {
            var handoff = new History
            {
                CorrelationId = "move:protected",
                EventType = HistoryEvents.ScanQueued,
                Outcome = HistoryOutcome.Requested,
                Source = "Move"
            };
            _db.History.Add(handoff);
            await _db.SaveChangesAsync();

            Assert.True(await _repository.DeleteAsync(handoff.Id));
            Assert.False(await _db.History.AnyAsync(history => history.Id == handoff.Id));
        }

        [Fact]
        public async Task DeleteAsync_TerminalScan_DoesNotDeleteOtherAuditEvents()
        {
            var handoff = new History
            {
                CorrelationId = "move:terminal",
                EventType = HistoryEvents.ScanQueued,
                Outcome = HistoryOutcome.Requested,
                Source = "Move"
            };
            var terminal = new History
            {
                CorrelationId = "move:terminal",
                EventType = HistoryEvents.ScanCompleted,
                Outcome = HistoryOutcome.Succeeded,
                Source = "LibraryScan"
            };
            _db.History.AddRange(handoff, terminal);
            await _db.SaveChangesAsync();

            Assert.True(await _repository.DeleteAsync(terminal.Id));
            Assert.True(await _db.History.AnyAsync(history => history.Id == handoff.Id));
            Assert.False(await _db.History.AnyAsync(history => history.Id == terminal.Id));
        }

        private static History Entry(string correlationId, string eventType, HistoryOutcome outcome, DateTime timestamp) =>
            new()
            {
                CorrelationId = correlationId,
                EventType = eventType,
                Outcome = outcome,
                Timestamp = timestamp,
                DownloadId = correlationId
            };

        public void Dispose() => _db.Dispose();
    }
}
