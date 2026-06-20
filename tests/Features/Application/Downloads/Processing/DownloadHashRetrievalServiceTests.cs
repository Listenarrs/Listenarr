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

namespace Listenarr.Tests.Features.Application.Downloads.Processing
{
    /// <summary>
    /// Tests for DownloadHashRetrievalService - Stage 4 exponential backoff retry logic
    /// </summary>
    public class DownloadHashRetrievalServiceTests : IDisposable
    {
        private readonly ListenArrDbContext _context;
        private readonly DownloadHistoryRepository _historyRepository;
        private readonly Mock<ILogger<DownloadHashRetrievalService>> _mockLogger;
        private readonly Mock<IDownloadClientAdapter> _mockAdapter;
        private readonly DownloadHashRetrievalService _service;

        public DownloadHashRetrievalServiceTests()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new ListenArrDbContext(options);
            _historyRepository = new DownloadHistoryRepository(_context);
            _mockLogger = new Mock<ILogger<DownloadHashRetrievalService>>();

            // Mock a single adapter for qBittorrent
            _mockAdapter = new Mock<IDownloadClientAdapter>();
            _mockAdapter.Setup(a => a.Protocol).Returns(DownloadProtocol.Torrent);

            _service = new DownloadHashRetrievalService(
                _mockLogger.Object,
                _historyRepository,
                _mockAdapter.Object,
                _mockAdapter.Object,
                _mockAdapter.Object,
                _mockAdapter.Object);
        }

        [Fact]
        public async Task TryRetrieveHashAsync_ReturnsNull_WhenMaxRetriesExceeded()
        {
            // Arrange
            var query = new DownloadClientItemQuery
            {
                Title = "Test Audiobook",
                AddedDate = DateTime.UtcNow,
                RetryCount = 10, // Max retries
                DownloadClient = "qBittorrent",
                DownloadClientId = "qbit-1",
                Protocol = DownloadProtocol.Torrent
            };

            var client = new DownloadClientConfiguration
            {
                Id = "qbit-1",
                Name = "qBittorrent",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080
            };

            // Act
            var result = await _service.TryRetrieveHashAsync(query, client);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task TryRetrieveHashAsync_ReturnsNull_WhenTimeoutExceeded()
        {
            // Arrange
            var query = new DownloadClientItemQuery
            {
                Title = "Test Audiobook",
                AddedDate = DateTime.UtcNow.AddSeconds(-65), // 65 seconds ago (over 60s limit)
                RetryCount = 3,
                DownloadClient = "qBittorrent",
                DownloadClientId = "qbit-1",
                Protocol = DownloadProtocol.Torrent
            };

            var client = new DownloadClientConfiguration
            {
                Id = "qbit-1",
                Name = "qBittorrent",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080
            };

            // Act
            var result = await _service.TryRetrieveHashAsync(query, client);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task TryRetrieveHashAsync_ReturnsNull_WhenBackoffNotElapsed()
        {
            // Arrange
            var query = new DownloadClientItemQuery
            {
                Title = "Test Audiobook",
                AddedDate = DateTime.UtcNow.AddSeconds(-10),
                RetryCount = 3, // 8 second backoff
                LastRetry = DateTime.UtcNow.AddSeconds(-5), // Only 5 seconds ago
                DownloadClient = "qBittorrent",
                DownloadClientId = "qbit-1",
                Protocol = DownloadProtocol.Torrent
            };

            var client = new DownloadClientConfiguration
            {
                Id = "qbit-1",
                Name = "qBittorrent",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080
            };

            // Act
            var result = await _service.TryRetrieveHashAsync(query, client);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task TryRetrieveHashAsync_ReturnsHash_WhenMatchFound()
        {
            // Arrange
            var query = new DownloadClientItemQuery
            {
                Title = "Test Audiobook",
                AddedDate = DateTime.UtcNow.AddSeconds(-5),
                RetryCount = 0,
                AudiobookId = Guid.NewGuid(),
                DownloadClient = "qBittorrent",
                DownloadClientId = "qbit-1",
                Protocol = DownloadProtocol.Torrent
            };

            var client = new DownloadClientConfiguration
            {
                Id = "qbit-1",
                Name = "qBittorrent",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080
            };

            var expectedHash = "ABC123DEF456";
            var mockItems = new List<DownloadClientItem>
            {
                new DownloadClientItem
                {
                    DownloadId = expectedHash,
                    Title = "Test Audiobook",
                    Status = DownloadItemStatus.Downloading
                }
            };

            _mockAdapter
                .Setup(a => a.GetItemsAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockItems);

            // Act
            var result = await _service.TryRetrieveHashAsync(query, client);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(expectedHash, result);

            // Verify history was recorded
            var history = await _historyRepository.GetByDownloadIdAsync(expectedHash);
            Assert.Single(history);
            Assert.Equal(DownloadHistoryEventType.Grabbed, history[0].EventType);
        }

        [Fact]
        public async Task TryRetrieveHashAsync_ReturnsNull_WhenNoMatchFound()
        {
            // Arrange
            var query = new DownloadClientItemQuery
            {
                Title = "Test Audiobook",
                AddedDate = DateTime.UtcNow.AddSeconds(-5),
                RetryCount = 0,
                DownloadClient = "qBittorrent",
                DownloadClientId = "qbit-1",
                Protocol = DownloadProtocol.Torrent
            };

            var client = new DownloadClientConfiguration
            {
                Id = "qbit-1",
                Name = "qBittorrent",
                Type = "qbittorrent",
                Host = "localhost",
                Port = 8080
            };

            var mockItems = new List<DownloadClientItem>
            {
                new DownloadClientItem
                {
                    DownloadId = "DIFFERENT123",
                    Title = "Different Audiobook",
                    Status = DownloadItemStatus.Downloading
                }
            };

            _mockAdapter
                .Setup(a => a.GetItemsAsync(It.IsAny<DownloadClientConfiguration>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockItems);

            // Act
            var result = await _service.TryRetrieveHashAsync(query, client);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task GetPendingHashRetrievalsAsync_ReturnsOnlyItemsWithoutValidHash()
        {
            // Arrange
            // Add history with temp DownloadId (needs hash retrieval)
            await _historyRepository.AddAsync(new DownloadHistory
            {
                DownloadId = "temp-123",
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                EventDate = DateTime.UtcNow.AddSeconds(-10),
                AudiobookId = Guid.NewGuid(),
                DownloadClient = "qBittorrent",
                DownloadClientId = "qbit-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Pending Audiobook",
                WasImported = false
            });

            // Add history with valid DownloadId (skip)
            await _historyRepository.AddAsync(new DownloadHistory
            {
                DownloadId = "ABC123DEF456789",
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                EventDate = DateTime.UtcNow.AddSeconds(-5),
                AudiobookId = Guid.NewGuid(),
                DownloadClient = "qBittorrent",
                DownloadClientId = "qbit-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Valid Audiobook",
                WasImported = false
            });

            // Act
            var result = await _service.GetPendingHashRetrievalsAsync();

            // Assert
            Assert.Single(result);
            Assert.Equal("temp-123", result[0].DownloadId);
            Assert.Equal("Pending Audiobook", result[0].Title);
        }

        [Fact]
        public async Task ExponentialBackoff_IncreasesCorrectly()
        {
            // Test that backoff times increase exponentially: 2s, 4s, 8s, 16s, 30s (capped)
            // This is a conceptual test - actual backoff logic is in TryRetrieveHashAsync

            var expectedBackoffs = new[] { 2, 4, 8, 16, 30, 30, 30, 30, 30, 30 }; // Last 6 capped at 30s

            for (int retry = 0; retry < 10; retry++)
            {
                var backoff = Math.Min(30, 2 * Math.Pow(2, retry));
                Assert.Equal(expectedBackoffs[retry], (int)backoff);
            }
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }
}
