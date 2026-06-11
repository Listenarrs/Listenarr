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

using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Services
{
    /// <summary>
    /// Tests for DownloadHistoryService - Event sourcing for idempotency
    /// </summary>
    public class DownloadHistoryServiceTests : IDisposable
    {
        private readonly ListenArrDbContext _context;
        private readonly DownloadHistoryService _service;
        private readonly Mock<ILogger<DownloadHistoryService>> _mockLogger;

        public DownloadHistoryServiceTests()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new ListenArrDbContext(options);
            _mockLogger = new Mock<ILogger<DownloadHistoryService>>();
            _service = new DownloadHistoryService(_context, _mockLogger.Object);
        }

        public void Dispose()
        {
            _context?.Dispose();
        }

        [Fact]
        public async Task IsAlreadyImportedAsync_WithNoHistory_ReturnsFalse()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";

            // Act
            var result = await _service.IsAlreadyImportedAsync(downloadId, clientId);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task IsAlreadyImportedAsync_WithImportedEvent_ReturnsTrue()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";

            var history = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Imported,
                EventDate = DateTime.UtcNow,
                WasImported = true
            };

            _context.Set<DownloadHistory>().Add(history);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.IsAlreadyImportedAsync(downloadId, clientId);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task IsAlreadyImportedAsync_CaseInsensitive_ReturnsTrue()
        {
            // Arrange - ID with lowercase
            var downloadIdLower = "abc123def456";
            var downloadIdUpper = "ABC123DEF456";
            var clientId = "qbittorrent-1";

            var history = new DownloadHistory
            {
                DownloadId = downloadIdUpper,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Imported,
                EventDate = DateTime.UtcNow,
                WasImported = true
            };

            _context.Set<DownloadHistory>().Add(history);
            await _context.SaveChangesAsync();

            // Act - query with lowercase
            var result = await _service.IsAlreadyImportedAsync(downloadIdLower, clientId);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task IsAlreadyImportedAsync_DifferentClient_ReturnsFalse()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId1 = "qbittorrent-1";
            var clientId2 = "transmission-1";

            var history = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId1,
                EventType = DownloadHistoryEventType.Imported,
                EventDate = DateTime.UtcNow,
                WasImported = true
            };

            _context.Set<DownloadHistory>().Add(history);
            await _context.SaveChangesAsync();

            // Act - query different client
            var result = await _service.IsAlreadyImportedAsync(downloadId, clientId2);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task HasRecentGrabbedAsync_WithRecentGrab_ReturnsTrue()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";
            var now = DateTime.UtcNow;

            var history = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Grabbed,
                EventDate = now.AddHours(-1), // 1 hour ago
                WasImported = false
            };

            _context.Set<DownloadHistory>().Add(history);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.HasRecentGrabbedAsync(downloadId, clientId, withinSeconds: 86400); // 24 hours

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task HasRecentGrabbedAsync_OldGrab_ReturnsFalse()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";
            var now = DateTime.UtcNow;

            var history = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Grabbed,
                EventDate = now.AddDays(-30), // 30 days ago
                WasImported = false
            };

            _context.Set<DownloadHistory>().Add(history);
            await _context.SaveChangesAsync();

            // Act
            var result = await _service.HasRecentGrabbedAsync(downloadId, clientId, withinSeconds: 604800); // 7 days

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task RecordGrabbedAsync_CreatesHistoryEntry()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";
            var title = "Test Audiobook";
            var audiobookId = Guid.NewGuid();

            // Act
            await _service.RecordGrabbedAsync(downloadId, clientId, title, DownloadProtocol.Torrent, audiobookId);

            // Assert
            var history = _context.Set<DownloadHistory>()
                .Where(h => h.DownloadId == downloadId.ToUpperInvariant() && h.DownloadClientId == clientId)
                .FirstOrDefault();

            Assert.NotNull(history);
            Assert.Equal(DownloadHistoryEventType.Grabbed, history.EventType);
            Assert.Equal(title, history.Title);
            Assert.Equal(audiobookId, history.AudiobookId);
            Assert.False(history.WasImported);
        }

        [Fact]
        public async Task RecordImportedAsync_SetsWasImportedFlag()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";
            var title = "Test Audiobook";
            var audiobookId = Guid.NewGuid();

            // Act
            await _service.RecordImportedAsync(downloadId, clientId, title, audiobookId);

            // Assert
            var history = _context.Set<DownloadHistory>()
                .Where(h => h.DownloadId == downloadId.ToUpperInvariant())
                .FirstOrDefault();

            Assert.NotNull(history);
            Assert.True(history.WasImported);
            Assert.NotNull(history.ImportedAt);
            Assert.Equal(DownloadHistoryEventType.Imported, history.EventType);
        }

        [Fact]
        public async Task GetHistoryAsync_ReturnsAllEventsOrderedByDate()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";
            var now = DateTime.UtcNow;

            var grabbed = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Grabbed,
                EventDate = now.AddHours(-3),
                WasImported = false
            };

            var completed = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.DownloadCompleted,
                EventDate = now.AddHours(-1),
                WasImported = false
            };

            var imported = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Imported,
                EventDate = now,
                WasImported = true
            };

            _context.Set<DownloadHistory>().AddRange(grabbed, completed, imported);
            await _context.SaveChangesAsync();

            // Act
            var history = await _service.GetHistoryAsync(downloadId, clientId);

            // Assert
            Assert.Equal(3, history.Count);
            Assert.Equal(DownloadHistoryEventType.Grabbed, history[0].EventType);
            Assert.Equal(DownloadHistoryEventType.DownloadCompleted, history[1].EventType);
            Assert.Equal(DownloadHistoryEventType.Imported, history[2].EventType);
        }

        [Fact]
        public async Task GetLatestEventAsync_ReturnsNewestEvent()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";
            var now = DateTime.UtcNow;

            var oldEvent = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Grabbed,
                EventDate = now.AddHours(-2),
                WasImported = false
            };

            var latestEvent = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Imported,
                EventDate = now,
                WasImported = true
            };

            _context.Set<DownloadHistory>().AddRange(oldEvent, latestEvent);
            await _context.SaveChangesAsync();

            // Act
            var latest = await _service.GetLatestEventAsync(downloadId, clientId);

            // Assert
            Assert.NotNull(latest);
            Assert.Equal(DownloadHistoryEventType.Imported, latest.EventType);
        }

        [Fact]
        public async Task RecordDownloadFailedAsync_StoresErrorMessage()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";
            var title = "Test Audiobook";
            var errorMsg = "Download failed: Connection timeout";

            // Act
            await _service.RecordDownloadFailedAsync(downloadId, clientId, title, errorMsg);

            // Assert
            var history = _context.Set<DownloadHistory>()
                .FirstOrDefault();

            Assert.NotNull(history);
            Assert.Equal(errorMsg, history.ErrorMessage);
            Assert.Equal(DownloadHistoryEventType.DownloadFailed, history.EventType);
        }

        [Fact]
        public async Task CleanupOldEntriesAsync_RemovesOldRecords()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";
            var now = DateTime.UtcNow;

            var oldHistory = new DownloadHistory
            {
                DownloadId = downloadId,
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Grabbed,
                EventDate = now.AddDays(-120), // 120 days old
                WasImported = false
            };

            var recentHistory = new DownloadHistory
            {
                DownloadId = downloadId + "2",
                DownloadClientId = clientId,
                EventType = DownloadHistoryEventType.Grabbed,
                EventDate = now.AddDays(-30), // 30 days old
                WasImported = false
            };

            _context.Set<DownloadHistory>().AddRange(oldHistory, recentHistory);
            await _context.SaveChangesAsync();

            // Act
            var deletedCount = await _service.CleanupOldEntriesAsync(retentionDays: 90);

            // Assert
            Assert.Equal(1, deletedCount);
            var remaining = _context.Set<DownloadHistory>().Count();
            Assert.Equal(1, remaining);
            Assert.Equal(downloadId + "2", remaining == 1 ? _context.Set<DownloadHistory>().First().DownloadId : null);
        }

        [Fact]
        public async Task RecordImportFailedAsync_StoresFailureReason()
        {
            // Arrange
            var downloadId = "ABC123DEF456";
            var clientId = "qbittorrent-1";
            var title = "Test Audiobook";
            var errorMsg = "File move failed: Permission denied";

            // Act
            await _service.RecordImportFailedAsync(downloadId, clientId, title, errorMsg);

            // Assert
            var history = _context.Set<DownloadHistory>()
                .FirstOrDefault();

            Assert.NotNull(history);
            Assert.Equal(errorMsg, history.ErrorMessage);
            Assert.Equal(DownloadHistoryEventType.ImportFailed, history.EventType);
            Assert.False(history.WasImported);
        }

        [Fact]
        public async Task NullOrEmptyDownloadId_ReturnsFalseForIsAlreadyImported()
        {
            // Act & Assert
            Assert.False(await _service.IsAlreadyImportedAsync(null, "client1"));
            Assert.False(await _service.IsAlreadyImportedAsync("", "client1"));
            Assert.False(await _service.IsAlreadyImportedAsync("  ", "client1"));
        }

        [Fact]
        public async Task GetHistoryAsync_WithNoHistory_ReturnsEmptyList()
        {
            // Act
            var history = await _service.GetHistoryAsync("NONEXISTENT", "client1");

            // Assert
            Assert.NotNull(history);
            Assert.Empty(history);
        }
    }
}
