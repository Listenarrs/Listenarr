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
    /// Tests for DownloadStateMachine - Stage 5 state transition validation
    /// </summary>
    public class DownloadStateMachineTests : IDisposable
    {
        private readonly ListenArrDbContext _context;
        private readonly DownloadHistoryRepository _historyRepository;
        private readonly Mock<ILogger<DownloadStateMachine>> _mockLogger;
        private readonly DownloadStateMachine _stateMachine;

        public DownloadStateMachineTests()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new ListenArrDbContext(options);
            _historyRepository = new DownloadHistoryRepository(_context);
            _mockLogger = new Mock<ILogger<DownloadStateMachine>>();

            _stateMachine = new DownloadStateMachine(_mockLogger.Object, _historyRepository);
        }

        [Theory]
        [InlineData(DownloadItemStatus.Queued, DownloadItemStatus.Downloading, true)]
        [InlineData(DownloadItemStatus.Downloading, DownloadItemStatus.Completed, true)]
        [InlineData(DownloadItemStatus.Completed, DownloadItemStatus.Failed, true)]
        [InlineData(DownloadItemStatus.Paused, DownloadItemStatus.Downloading, true)]
        [InlineData(DownloadItemStatus.Failed, DownloadItemStatus.Queued, true)]
        [InlineData(DownloadItemStatus.Queued, DownloadItemStatus.Completed, false)] // Invalid: skip downloading
        [InlineData(DownloadItemStatus.Completed, DownloadItemStatus.Queued, false)] // Invalid: can't go backward
        public void IsValidTransition_ValidatesCorrectly(DownloadItemStatus from, DownloadItemStatus to, bool expected)
        {
            // Act
            var result = _stateMachine.IsValidTransition(from, to);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void IsValidTransition_AllowsSameState()
        {
            // Arrange & Act & Assert
            Assert.True(_stateMachine.IsValidTransition(DownloadItemStatus.Downloading, DownloadItemStatus.Downloading));
            Assert.True(_stateMachine.IsValidTransition(DownloadItemStatus.Queued, DownloadItemStatus.Queued));
            Assert.True(_stateMachine.IsValidTransition(DownloadItemStatus.Completed, DownloadItemStatus.Completed));
        }

        [Fact]
        public async Task TransitionAsync_RecordsValidTransition()
        {
            // Arrange
            var downloadId = "TEST123";

            // Act
            var result = await _stateMachine.TransitionAsync(
                downloadId: downloadId,
                currentState: DownloadItemStatus.Queued,
                newState: DownloadItemStatus.Downloading,
                eventType: DownloadHistoryEventType.Downloading,
                audiobookId: Guid.NewGuid(),
                downloadClient: "qBittorrent",
                downloadClientId: "qbit-1",
                protocol: DownloadProtocol.Torrent,
                title: "Test Audiobook");

            // Assert
            Assert.True(result);

            var history = await _historyRepository.GetByDownloadIdAsync(downloadId);
            Assert.Single(history);
            Assert.Equal(DownloadItemStatus.Downloading, history[0].Status);
            Assert.Equal(DownloadHistoryEventType.Downloading, history[0].EventType);
        }

        [Fact]
        public async Task TransitionAsync_RejectsInvalidTransition()
        {
            // Arrange
            var downloadId = "TEST456";

            // Act - Try invalid transition: Queued → Completed (skips Downloading)
            var result = await _stateMachine.TransitionAsync(
                downloadId: downloadId,
                currentState: DownloadItemStatus.Queued,
                newState: DownloadItemStatus.Completed,
                eventType: DownloadHistoryEventType.DownloadCompleted,
                downloadClient: "qBittorrent",
                downloadClientId: "qbit-1");

            // Assert
            Assert.False(result);

            // Verify no history was recorded
            var history = await _historyRepository.GetByDownloadIdAsync(downloadId);
            Assert.Empty(history);
        }

        [Fact]
        public async Task GetCurrentStateAsync_ReturnsLatestState()
        {
            // Arrange
            var downloadId = "STATE123";
            await _historyRepository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                EventDate = DateTime.UtcNow.AddMinutes(-2),
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            await _historyRepository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Downloading,
                Status = DownloadItemStatus.Downloading,
                EventDate = DateTime.UtcNow,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            // Act
            var currentState = await _stateMachine.GetCurrentStateAsync(downloadId);

            // Assert
            Assert.NotNull(currentState);
            Assert.Equal(DownloadItemStatus.Downloading, currentState!.Value);
        }

        [Fact]
        public void GetValidNextStates_ReturnsCorrectStates()
        {
            // Act
            var fromQueued = _stateMachine.GetValidNextStates(DownloadItemStatus.Queued);
            var fromDownloading = _stateMachine.GetValidNextStates(DownloadItemStatus.Downloading);

            // Assert
            Assert.Contains(DownloadItemStatus.Downloading, fromQueued);
            Assert.Contains(DownloadItemStatus.Paused, fromQueued);
            Assert.Contains(DownloadItemStatus.Failed, fromQueued);

            Assert.Contains(DownloadItemStatus.Completed, fromDownloading);
            Assert.Contains(DownloadItemStatus.Paused, fromDownloading);
            Assert.Contains(DownloadItemStatus.Failed, fromDownloading);
        }

        [Fact]
        public async Task GetTransitionHistoryAsync_ReturnsAllTransitions()
        {
            // Arrange
            var downloadId = "HISTORY123";

            // Create a sequence of state transitions
            await _historyRepository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Grabbed,
                Status = DownloadItemStatus.Queued,
                EventDate = DateTime.UtcNow.AddMinutes(-3),
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            await _historyRepository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.Downloading,
                Status = DownloadItemStatus.Downloading,
                EventDate = DateTime.UtcNow.AddMinutes(-2),
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            await _historyRepository.AddAsync(new DownloadHistory
            {
                DownloadId = downloadId,
                EventType = DownloadHistoryEventType.DownloadCompleted,
                Status = DownloadItemStatus.Completed,
                EventDate = DateTime.UtcNow,
                DownloadClient = "Test",
                DownloadClientId = "test-1",
                Protocol = DownloadProtocol.Torrent,
                Title = "Test"
            });

            // Act
            var transitions = await _stateMachine.GetTransitionHistoryAsync(downloadId);

            // Assert
            Assert.Equal(2, transitions.Count);
            Assert.Equal(DownloadItemStatus.Queued, transitions[0].FromState);
            Assert.Equal(DownloadItemStatus.Downloading, transitions[0].ToState);
            Assert.Equal(DownloadItemStatus.Downloading, transitions[1].FromState);
            Assert.Equal(DownloadItemStatus.Completed, transitions[1].ToState);
        }

        [Theory]
        [InlineData(new[] { DownloadItemStatus.Queued, DownloadItemStatus.Downloading, DownloadItemStatus.Completed }, true)]
        [InlineData(new[] { DownloadItemStatus.Downloading, DownloadItemStatus.Paused, DownloadItemStatus.Downloading }, true)]
        [InlineData(new[] { DownloadItemStatus.Queued, DownloadItemStatus.Completed }, false)] // Invalid: skips Downloading
        [InlineData(new[] { DownloadItemStatus.Completed, DownloadItemStatus.Queued }, false)] // Invalid: backward
        public void ValidateTransitionSequence_ValidatesCorrectly(DownloadItemStatus[] sequence, bool expected)
        {
            // Act
            var result = _stateMachine.ValidateTransitionSequence(sequence);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ValidateTransitionSequence_HandlesEmptySequence()
        {
            // Act
            var result = _stateMachine.ValidateTransitionSequence(Array.Empty<DownloadItemStatus>());

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task TransitionAsync_IncludesMetadata()
        {
            // Arrange
            var downloadId = "META123";
            var metadata = new Dictionary<string, object>
            {
                ["RetryCount"] = 3,
                ["ClientVersion"] = "4.5.0"
            };

            // Act
            await _stateMachine.TransitionAsync(
                downloadId: downloadId,
                currentState: DownloadItemStatus.Queued,
                newState: DownloadItemStatus.Downloading,
                eventType: DownloadHistoryEventType.Downloading,
                downloadClient: "qBittorrent",
                downloadClientId: "qbit-1",
                metadata: metadata);

            // Assert
            var history = await _historyRepository.GetByDownloadIdAsync(downloadId);
            Assert.Single(history);
            Assert.NotNull(history[0].Data);
            Assert.Equal(3, history[0].Data["RetryCount"]);
        }

        [Fact]
        public async Task CompleteWorkflow_ValidatesEntireSequence()
        {
            // Arrange - Simulate complete download workflow
            var downloadId = "WORKFLOW123";

            // Act & Assert - Each transition should succeed
            Assert.True(await _stateMachine.TransitionAsync(
                downloadId, DownloadItemStatus.Queued, DownloadItemStatus.Queued,
                DownloadHistoryEventType.Grabbed, downloadClient: "Test", downloadClientId: "test-1"));

            Assert.True(await _stateMachine.TransitionAsync(
                downloadId, DownloadItemStatus.Queued, DownloadItemStatus.Downloading,
                DownloadHistoryEventType.Downloading, downloadClient: "Test", downloadClientId: "test-1"));

            Assert.True(await _stateMachine.TransitionAsync(
                downloadId, DownloadItemStatus.Downloading, DownloadItemStatus.Completed,
                DownloadHistoryEventType.DownloadCompleted, downloadClient: "Test", downloadClientId: "test-1"));

            // Verify complete history
            var transitions = await _stateMachine.GetTransitionHistoryAsync(downloadId);
            Assert.Equal(2, transitions.Count);
        }

        public void Dispose()
        {
            _context?.Dispose();
        }
    }
}
