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

using Xunit;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Features.Domain.Models
{
    /// <summary>
    /// Tests for the DownloadClientItem model
    /// Stage 1: Verify standardized model works correctly
    /// </summary>
    public class DownloadClientItemTests
    {
        [Fact]
        public void DownloadClientItem_InitializesWithDefaults()
        {
            // Arrange & Act
            var item = new DownloadClientItem();

            // Assert
            Assert.NotNull(item.DownloadId);
            Assert.Equal(string.Empty, item.DownloadId);
            Assert.Equal(string.Empty, item.Title);
            Assert.Equal(string.Empty, item.Category);
            Assert.Equal(DownloadItemStatus.Queued, item.Status);
            Assert.False(item.IsEncrypted);
            Assert.False(item.CanBeRemoved);
            Assert.False(item.CanMoveFiles);
            Assert.False(item.Removed);
        }

        [Fact]
        public void DownloadClientItem_Clone_CreatesShallowCopy()
        {
            // Arrange
            var original = new DownloadClientItem
            {
                DownloadId = "ABC123",
                Title = "Test Download",
                Status = DownloadItemStatus.Downloading,
                TotalSize = 1000000,
                Progress = 50.5
            };

            // Act
            var clone = original.Clone();

            // Assert
            Assert.NotSame(original, clone);
            Assert.Equal(original.DownloadId, clone.DownloadId);
            Assert.Equal(original.Title, clone.Title);
            Assert.Equal(original.Status, clone.Status);
            Assert.Equal(original.TotalSize, clone.TotalSize);
            Assert.Equal(original.Progress, clone.Progress);
        }

        [Fact]
        public void IsComplete_ReturnsTrueWhenCompleted()
        {
            // Arrange
            var item = new DownloadClientItem
            {
                Status = DownloadItemStatus.Completed,
                RemainingSize = 0
            };

            // Act & Assert
            Assert.True(item.IsComplete());
        }

        [Fact]
        public void IsComplete_ReturnsFalseWhenNotCompleted()
        {
            // Arrange
            var item = new DownloadClientItem
            {
                Status = DownloadItemStatus.Downloading,
                RemainingSize = 1000
            };

            // Act & Assert
            Assert.False(item.IsComplete());
        }

        [Fact]
        public void HasFailed_ReturnsTrueWhenFailed()
        {
            // Arrange
            var item = new DownloadClientItem
            {
                Status = DownloadItemStatus.Failed
            };

            // Act & Assert
            Assert.True(item.HasFailed());
        }

        [Fact]
        public void IsDownloading_ReturnsTrueWhenDownloading()
        {
            // Arrange
            var item = new DownloadClientItem
            {
                Status = DownloadItemStatus.Downloading
            };

            // Act & Assert
            Assert.True(item.IsDownloading());
        }

        [Theory]
        [InlineData(DownloadItemStatus.Queued)]
        [InlineData(DownloadItemStatus.Paused)]
        [InlineData(DownloadItemStatus.Completed)]
        [InlineData(DownloadItemStatus.Failed)]
        [InlineData(DownloadItemStatus.Warning)]
        public void Status_CanBeSetToAnyValidValue(DownloadItemStatus status)
        {
            // Arrange
            var item = new DownloadClientItem();

            // Act
            item.Status = status;

            // Assert
            Assert.Equal(status, item.Status);
        }

        [Fact]
        public void DownloadClientItemClientInfo_FromClient_CreatesCorrectly()
        {
            // Arrange & Act
            var clientInfo = DownloadClientItemClientInfo.FromClient(
                clientId: "qbit-1",
                clientName: "qBittorrent",
                clientType: "qbittorrent",
                protocol: DownloadProtocol.Torrent,
                removeCompletedDownloads: true,
                hasPostImportCategory: true
            );

            // Assert
            Assert.Equal("qbit-1", clientInfo.Id);
            Assert.Equal("qBittorrent", clientInfo.Name);
            Assert.Equal("qbittorrent", clientInfo.Type);
            Assert.Equal(DownloadProtocol.Torrent, clientInfo.Protocol);
            Assert.True(clientInfo.RemoveCompletedDownloads);
            Assert.True(clientInfo.HasPostImportCategory);
        }

        [Fact]
        public void DownloadClientItem_SupportsRichMetadata()
        {
            // Arrange & Act
            var item = new DownloadClientItem
            {
                DownloadId = "A1B2C3D4E5F6",
                Title = "Audiobook Name",
                Category = "audiobooks",
                TotalSize = 500000000,
                RemainingSize = 250000000,
                RemainingTime = TimeSpan.FromMinutes(30),
                SeedRatio = 1.5,
                OutputPath = "/downloads/audiobook",
                Status = DownloadItemStatus.Downloading,
                Message = "Downloading at full speed",
                Progress = 50.0,
                DownloadSpeed = 1048576,
                Seeders = 10,
                Leechers = 5,
                DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                    "qbit-1", "qBittorrent", "qbittorrent", DownloadProtocol.Torrent)
            };

            // Assert
            Assert.Equal("A1B2C3D4E5F6", item.DownloadId);
            Assert.Equal("Audiobook Name", item.Title);
            Assert.Equal("audiobooks", item.Category);
            Assert.Equal(500000000, item.TotalSize);
            Assert.Equal(250000000, item.RemainingSize);
            Assert.Equal(TimeSpan.FromMinutes(30), item.RemainingTime);
            Assert.Equal(1.5, item.SeedRatio);
            Assert.Equal("/downloads/audiobook", item.OutputPath);
            Assert.Equal(DownloadItemStatus.Downloading, item.Status);
            Assert.Equal("Downloading at full speed", item.Message);
            Assert.Equal(50.0, item.Progress);
            Assert.Equal(1048576, item.DownloadSpeed);
            Assert.Equal(10, item.Seeders);
            Assert.Equal(5, item.Leechers);
            Assert.NotNull(item.DownloadClientInfo);
            Assert.Equal(DownloadProtocol.Torrent, item.DownloadClientInfo.Protocol);
        }
    }
}
