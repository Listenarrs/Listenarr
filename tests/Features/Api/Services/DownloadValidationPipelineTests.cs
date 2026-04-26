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

using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    /// <summary>
    /// Tests for DownloadValidationPipeline - Stage 6 three-phase validation
    /// </summary>
    public class DownloadValidationPipelineTests : IDisposable
    {
        private readonly ListenArrDbContext _context;
        private readonly DownloadHistoryRepository _historyRepository;
        private readonly DownloadStateMachine _stateMachine;
        private readonly Mock<ILogger<DownloadValidationPipeline>> _mockLogger;
        private readonly Mock<ILogger<DownloadStateMachine>> _mockStateMachineLogger;
        private readonly DownloadValidationPipeline _pipeline;
        private readonly string _testDirectory;

        public DownloadValidationPipelineTests()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new ListenArrDbContext(options);
            _historyRepository = new DownloadHistoryRepository(_context);
            _mockStateMachineLogger = new Mock<ILogger<DownloadStateMachine>>();
            _stateMachine = new DownloadStateMachine(_mockStateMachineLogger.Object, _historyRepository);
            _mockLogger = new Mock<ILogger<DownloadValidationPipeline>>();

            _pipeline = new DownloadValidationPipeline(_mockLogger.Object, _stateMachine, _historyRepository);

            // Create test directory
            _testDirectory = Path.Join(Path.GetTempPath(), "listenarr_pipeline_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDirectory);
        }

        [Fact]
        public async Task ExecutePipeline_SucceedsWithValidDownload()
        {
            // Arrange
            var testFile = Path.Join(_testDirectory, "test.m4b");
            File.WriteAllText(testFile, "test content");

            var download = new DownloadClientItem
            {
                DownloadId = "VALID123",
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Completed,
                OutputPath = testFile,
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Id = "qbit-1",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            var result = await _pipeline.ExecutePipelineAsync(download);

            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.CheckPhase);
            Assert.True(result.CheckPhase.Success);
            Assert.NotNull(result.ImportPhase);
            Assert.True(result.ImportPhase.Success);
            Assert.NotNull(result.VerifyPhase);
            Assert.True(result.VerifyPhase.Success);
            Assert.NotNull(result.CompletedAt);

            // Verify history was recorded
            var history = await _historyRepository.GetByDownloadIdAsync("VALID123");
            Assert.NotEmpty(history);

            // Verify marked as imported
            var wasImported = await _historyRepository.WasImportedAsync("VALID123");
            Assert.True(wasImported);
        }

        [Fact]
        public async Task CheckPhase_FailsWhenStatusNotCompleted()
        {
            // Arrange
            var download = new DownloadClientItem
            {
                DownloadId = "NOTCOMPLETE123",
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Downloading, // Not completed
                OutputPath = _testDirectory,
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            var result = await _pipeline.ExecutePipelineAsync(download);

            // Assert
            Assert.False(result.Success);
            Assert.NotNull(result.CheckPhase);
            Assert.False(result.CheckPhase.Success);
            Assert.Contains("not completed", result.CheckPhase.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.ImportPhase); // Should not reach import phase
            Assert.Null(result.VerifyPhase); // Should not reach verify phase
        }

        [Fact]
        public async Task CheckPhase_FailsWhenOutputPathEmpty()
        {
            // Arrange
            var download = new DownloadClientItem
            {
                DownloadId = "NOPATH123",
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Completed,
                OutputPath = string.Empty, // Empty path
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            var result = await _pipeline.ExecutePipelineAsync(download);

            // Assert
            Assert.False(result.Success);
            Assert.NotNull(result.CheckPhase);
            Assert.False(result.CheckPhase.Success);
            Assert.Contains("empty", result.CheckPhase.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CheckPhase_FailsWhenOutputPathDoesNotExist()
        {
            // Arrange
            var nonExistentPath = Path.Join(_testDirectory, "nonexistent", "file.m4b");

            var download = new DownloadClientItem
            {
                DownloadId = "BADPATH123",
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Completed,
                OutputPath = nonExistentPath,
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            var result = await _pipeline.ExecutePipelineAsync(download);

            // Assert
            Assert.False(result.Success);
            Assert.NotNull(result.CheckPhase);
            Assert.False(result.CheckPhase.Success);
            Assert.Contains("does not exist", result.CheckPhase.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CheckPhase_FailsWhenDownloadIdTemporary()
        {
            // Arrange
            var testFile = Path.Join(_testDirectory, "test2.m4b");
            File.WriteAllText(testFile, "test");

            var download = new DownloadClientItem
            {
                DownloadId = "temp-12345", // Temporary ID
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Completed,
                OutputPath = testFile,
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            var result = await _pipeline.ExecutePipelineAsync(download);

            // Assert
            Assert.False(result.Success);
            Assert.NotNull(result.CheckPhase);
            Assert.False(result.CheckPhase.Success);
            Assert.Contains("Invalid or temporary", result.CheckPhase.ErrorMessage);
        }

        [Fact]
        public async Task CheckPhase_FailsWhenSizeZero()
        {
            // Arrange
            var testFile = Path.Join(_testDirectory, "test3.m4b");
            File.WriteAllText(testFile, "test");

            var download = new DownloadClientItem
            {
                DownloadId = "ZEROSIZE123",
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Completed,
                OutputPath = testFile,
                TotalSize = 0, // Zero size
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            var result = await _pipeline.ExecutePipelineAsync(download);

            // Assert
            Assert.False(result.Success);
            Assert.NotNull(result.CheckPhase);
            Assert.False(result.CheckPhase.Success);
            Assert.Contains("zero or negative", result.CheckPhase.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Pipeline_RecordsAllPhases()
        {
            // Arrange
            var testFile = Path.Join(_testDirectory, "test4.m4b");
            File.WriteAllText(testFile, "test content");

            var download = new DownloadClientItem
            {
                DownloadId = "PHASES123",
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Completed,
                OutputPath = testFile,
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Id = "qbit-1",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            await _pipeline.ExecutePipelineAsync(download);

            // Assert - Verify all phases were recorded in history
            var history = await _historyRepository.GetByDownloadIdAsync("PHASES123");
            Assert.True(history.Count >= 3); // At least 3 phase records

            // Check for phase metadata
            var checkEvent = history.FirstOrDefault(h =>
                h.Data != null && h.Data.TryGetValue("Phase", out var phaseObj) && phaseObj?.ToString() == "Check");
            Assert.NotNull(checkEvent);

            var importEvent = history.FirstOrDefault(h =>
                h.Data != null && h.Data.TryGetValue("Phase", out var phaseObj) && phaseObj?.ToString() == "Import");
            Assert.NotNull(importEvent);

            var verifyEvent = history.FirstOrDefault(h =>
                h.Data != null && h.Data.TryGetValue("Phase", out var phaseObj) && phaseObj?.ToString() == "Verify");
            Assert.NotNull(verifyEvent);
        }

        [Fact]
        public async Task Pipeline_IncludesAudiobookId()
        {
            // Arrange
            var testFile = Path.Join(_testDirectory, "test5.m4b");
            File.WriteAllText(testFile, "test");

            var audiobookId = Guid.NewGuid();
            var download = new DownloadClientItem
            {
                DownloadId = "AUDIOBOOK123",
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Completed,
                OutputPath = testFile,
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Id = "qbit-1",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            await _pipeline.ExecutePipelineAsync(download, audiobookId);

            // Assert
            var history = await _historyRepository.GetByAudiobookIdAsync(audiobookId);
            Assert.NotEmpty(history);
        }

        [Fact]
        public async Task Pipeline_CalculatesDuration()
        {
            // Arrange
            var testFile = Path.Join(_testDirectory, "test6.m4b");
            File.WriteAllText(testFile, "test");

            var download = new DownloadClientItem
            {
                DownloadId = "DURATION123",
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Completed,
                OutputPath = testFile,
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            var result = await _pipeline.ExecutePipelineAsync(download);

            // Assert
            Assert.True(result.Duration.TotalSeconds >= 0);
            Assert.True(result.Duration.TotalSeconds < 10); // Should complete quickly
        }

        [Fact]
        public async Task ImportPhase_SetsImportedPath()
        {
            // Arrange
            var testFile = Path.Join(_testDirectory, "test7.m4b");
            File.WriteAllText(testFile, "test");

            var download = new DownloadClientItem
            {
                DownloadId = "IMPORTPATH123",
                Title = "Test Audiobook",
                Status = DownloadItemStatus.Completed,
                OutputPath = testFile,
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Name = "qBittorrent",
                    Type = "qBittorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            };

            // Act
            var result = await _pipeline.ExecutePipelineAsync(download);

            // Assert
            Assert.NotNull(result.ImportPhase);
            Assert.NotNull(result.ImportPhase.ImportedPath);
            Assert.Equal(testFile, result.ImportPhase.ImportedPath);
        }

        public void Dispose()
        {
            _context?.Dispose();

            // Cleanup test directory
            if (Directory.Exists(_testDirectory))
            {
                try
                {
                    Directory.Delete(_testDirectory, true);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}
