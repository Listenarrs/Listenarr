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

using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.ActivityHistory.Persistence
{
    /// <summary>
    /// Tests for DownloadHistoryRepository - Stage 3 event-sourced tracking
    /// </summary>
    public class DownloadHistoryRepositoryTests : IDisposable
    {
        private readonly ListenArrDbContext _context;
        private readonly DownloadHistoryRepository _repository;

        public DownloadHistoryRepositoryTests()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new ListenArrDbContext(options);
            _repository = new DownloadHistoryRepository(_context);
        }

        [Fact]
        public async Task AddAsync_CreatesHistoryEvent()
        {
            // Arrange
            var history = new DownloadHistory
            {
                DownloadId = "ABC123",
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                EventDate = DateTime.UtcNow,
                AudiobookId = Guid.NewGuid(),
                DownloadClient = "qBittorrent",
                DownloadClientId = "qbit-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test Audiobook",
                OutputPath = "/downloads/test"
            };

            // Act
            var result = await _repository.AddAsync(history);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Id > 0);
            Assert.Equal("ABC123", result.DownloadId);
            Assert.Equal(DownloadHistoryEventType.Grabbed, result.EventType);
        }

        [Fact]
        public async Task GetByDownloadIdAsync_ReturnsAllEventsForDownload()
        {
            // Arrange
            var downloadId = "TEST123";
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Downloading,
                Status = DownloadItemStatus.Downloading,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            // Act
            var events = await _repository.GetByDownloadIdAsync(downloadId);

            // Assert
            Assert.Equal(2, events.Count);
            Assert.Equal(DownloadHistoryEventType.Downloading, events[0].EventType); // Most recent first
            Assert.Equal(DownloadHistoryEventType.Grabbed, events[1].EventType);
        }

        [Fact]
        public async Task GetLatestEventAsync_ReturnsNewestEvent()
        {
            // Arrange
            var downloadId = "LATEST123";
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                EventDate = DateTime.UtcNow.AddMinutes(-10),
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Imported,
                Status = DownloadItemStatus.Completed,
                EventDate = DateTime.UtcNow,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            // Act
            var latest = await _repository.GetLatestEventAsync(downloadId);

            // Assert
            Assert.NotNull(latest);
            Assert.Equal(DownloadHistoryEventType.Imported, latest.EventType);
        }

        [Fact]
        public async Task WasImportedAsync_ReturnsTrueWhenImported()
        {
            // Arrange
            var downloadId = "IMPORTED123";
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Imported,
                Status = DownloadItemStatus.Completed,
                WasImported = true,
                ImportedAt = DateTime.UtcNow,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            // Act
            var wasImported = await _repository.WasImportedAsync(downloadId);

            // Assert
            Assert.True(wasImported);
        }

        [Fact]
        public async Task WasImportedAsync_ReturnsFalseWhenNotImported()
        {
            // Arrange
            var downloadId = "NOTIMPORTED123";
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                WasImported = false,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            // Act
            var wasImported = await _repository.WasImportedAsync(downloadId);

            // Assert
            Assert.False(wasImported);
        }

        [Fact]
        public async Task MarkAsImportedAsync_AppendsImportedEventWithoutRewritingEarlierEvents()
        {
            // Arrange
            var downloadId = "MARK123";
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.DownloadCompleted,
                Status = DownloadItemStatus.Completed,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            // Act
            await _repository.MarkAsImportedAsync(downloadId);

            // Assert
            var events = await _repository.GetByDownloadIdAsync(downloadId);
            Assert.Equal(3, events.Count);
            Assert.Single(events, e => e.EventType == DownloadHistoryEventType.Imported && e.WasImported);
            Assert.Equal(2, events.Count(e => !e.WasImported));
        }

        [Fact]
        public async Task GetPendingImportsAsync_ReturnsOnlyGrabbedNotImported()
        {
            // Arrange
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = "PENDING1",
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                WasImported = false,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test1"
            });
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = "PENDING2",
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                WasImported = true,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test2"
            });
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = "PENDING2",
                EventType = DownloadHistoryEventType.Imported,
                Status = DownloadItemStatus.Imported,
                WasImported = true,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test2"
            });

            // Act
            var pending = await _repository.GetPendingImportsAsync();

            // Assert
            Assert.Single(pending);
            Assert.Equal("PENDING1", pending[0].DownloadId);
        }

        [Fact]
        public async Task GetFailedDownloadsAsync_ReturnsFailuresAfterDate()
        {
            // Arrange
            var cutoff = DateTime.UtcNow.AddHours(-1);
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = "FAIL1",
                EventType = DownloadHistoryEventType.DownloadFailed,
                Status = DownloadItemStatus.Failed,
                EventDate = DateTime.UtcNow,
                ErrorMessage = "Download failed",
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = "FAIL2",
                EventType = DownloadHistoryEventType.DownloadFailed,
                Status = DownloadItemStatus.Failed,
                EventDate = cutoff.AddHours(-2),
                ErrorMessage = "Old failure",
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            // Act
            var failures = await _repository.GetFailedDownloadsAsync(cutoff);

            // Assert
            Assert.Single(failures);
            Assert.Equal("FAIL1", failures[0].DownloadId);
        }

        [Fact]
        public async Task DownloadId_IsCaseInsensitive()
        {
            // Arrange
            var downloadId = "CaseSensitive123";
            await _repository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId.ToLowerInvariant(),
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            // Act - Search with uppercase
            var events = await _repository.GetByDownloadIdAsync(downloadId.ToUpperInvariant());

            // Assert
            Assert.Single(events);
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }
}
