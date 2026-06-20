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

namespace Listenarr.Tests.Features.Infrastructure.ActivityHistory.Services
{
    public class DownloadHistoryServiceTests : IDisposable
    {
        private readonly ListenArrDbContext _context;
        private readonly DownloadHistoryService _service;

        public DownloadHistoryServiceTests()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            _context = new ListenArrDbContext(options);
            _service = new DownloadHistoryService(
                _context,
                new Mock<ILogger<DownloadHistoryService>>().Object);
        }

        [Fact]
        public async Task RecordGrabbedAsync_WritesCanonicalHistory()
        {
            await _service.RecordGrabbedAsync(
                "abc123",
                "client-1",
                "Test Book",
                DownloadProtocol.Torrent);

            var entry = Assert.Single(_context.History);
            Assert.Equal("ABC123", entry.DownloadId);
            Assert.Equal(HistoryEvents.Grabbed, entry.EventType);
            Assert.Equal(HistoryOutcome.Succeeded, entry.Outcome);
            Assert.Equal("Test Book", entry.SourceTitle);
        }

        [Fact]
        public async Task RecordImportedAsync_IsAppendOnlyAndDrivesIdempotency()
        {
            await _service.RecordGrabbedAsync("abc123", "client-1", "Test Book", DownloadProtocol.Torrent);
            await _service.RecordImportedAsync("abc123", "client-1", "Test Book");

            Assert.Equal(2, await _context.History.CountAsync());
            Assert.True(await _service.IsAlreadyImportedAsync("AbC123", "client-1"));
            Assert.Equal(HistoryEvents.Imported, (await _service.GetLatestEventAsync("abc123", "client-1"))!.EventType.ToString());
        }

        [Fact]
        public async Task RecordFailure_PreservesErrorAndFailedOutcome()
        {
            await _service.RecordImportFailedAsync("abc123", "client-1", "Test Book", "permission denied");

            var entry = Assert.Single(_context.History);
            Assert.Equal(HistoryEvents.ImportFailed, entry.EventType);
            Assert.Equal(HistoryOutcome.Failed, entry.Outcome);
            Assert.Equal("permission denied", entry.Error);
        }

        [Fact]
        public async Task GetHistoryAsync_ReturnsEventsInChronologicalOrder()
        {
            await _service.RecordGrabbedAsync("abc123", "client-1", "Test Book", DownloadProtocol.Torrent);
            await _service.RecordDownloadCompleteAsync("abc123", "client-1", "Test Book");
            await _service.RecordImportedAsync("abc123", "client-1", "Test Book");

            var history = await _service.GetHistoryAsync("abc123", "client-1");

            Assert.Collection(
                history,
                entry => Assert.Equal(DownloadHistoryEventType.Grabbed, entry.EventType),
                entry => Assert.Equal(DownloadHistoryEventType.DownloadCompleted, entry.EventType),
                entry => Assert.Equal(DownloadHistoryEventType.Imported, entry.EventType));
        }

        [Fact]
        public async Task HasRecentGrabbedAsync_UsesCanonicalHistory()
        {
            await _service.RecordGrabbedAsync("abc123", "client-1", "Test Book", DownloadProtocol.Torrent);

            Assert.True(await _service.HasRecentGrabbedAsync("abc123", "client-1"));
            Assert.False(await _service.HasRecentGrabbedAsync("abc123", "other-client"));
        }

        [Fact]
        public async Task CleanupOldEntriesAsync_DeletesOnlyDownloadHistoryBeforeCutoff()
        {
            _context.History.AddRange(
                new History
                {
                    DownloadId = "OLD",
                    DownloadClientId = "client-1",
                    EventType = HistoryEvents.Grabbed,
                    Timestamp = DateTime.UtcNow.AddDays(-120),
                    CorrelationId = "old"
                },
                new History
                {
                    EventType = HistoryEvents.LibraryUpdated,
                    Timestamp = DateTime.UtcNow.AddDays(-120),
                    CorrelationId = "library"
                },
                new History
                {
                    DownloadId = "RECENT",
                    DownloadClientId = "client-1",
                    EventType = HistoryEvents.Grabbed,
                    Timestamp = DateTime.UtcNow.AddDays(-1),
                    CorrelationId = "recent"
                });
            await _context.SaveChangesAsync();

            var deleted = await _service.CleanupOldEntriesAsync(90);

            Assert.Equal(1, deleted);
            Assert.Equal(2, await _context.History.CountAsync());
        }

        [Fact]
        public async Task CleanupOldEntriesAsync_ZeroRetentionPreservesAllHistory()
        {
            await _service.RecordGrabbedAsync(
                "unlimited-history",
                "client-1",
                "Unlimited History",
                DownloadProtocol.Torrent);

            var deleted = await _service.CleanupOldEntriesAsync();

            Assert.Equal(0, deleted);
            Assert.Single(await _service.GetHistoryAsync("unlimited-history", "client-1"));
        }

        public void Dispose() => _context.Dispose();
    }
}
