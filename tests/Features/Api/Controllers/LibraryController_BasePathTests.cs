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
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Listenarr.Api.Controllers;
using Listenarr.Domain.Models;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Common;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_BasePathTests
    {
        [Fact]
        public void ComputeAudiobookBaseDirectoryFromPattern_NonSeriesBook_ReturnsCorrectPath()
        {
            // Arrange
            var audiobook = new Audiobook
            {
                Title = "The Buffalo Hunter Hunter",
                Authors = new List<string> { "Stephen Graham Jones" },
                PublishYear = "2025",
                Series = null, // No series
                SeriesNumber = null
            };

            var rootPath = "/server/mnt/drive/Audiobooks";
            var fileNamingPattern = "{Author}/{Series}/{Title}"; // Default pattern

            // Mock dependencies
            var mockRepo = new Mock<IAudiobookRepository>();
            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();
            var mockScanQueue = new Mock<IScanQueueService>();

            var mockFileNamingService = new Mock<IFileNamingService>();
            mockFileNamingService
                .Setup(x => x.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool sanitize) =>
                {
                    // For non-series book, the method derives pattern from fileNamingPattern by removing file-specific tokens
                    // Input: "{Author}/{Series}/{Title}"
                    // Output after processing: "{Author}/{Title}" (Series removed since empty)
                    if (pattern == "{Author}/{Title}")
                    {
                        return "Stephen Graham Jones/The Buffalo Hunter Hunter";
                    }
                    return pattern;
                });

            var scopeFactory = new Mock<IServiceScopeFactory>();

            // Create controller instance
            var controller = new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory.Object,
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                mockFileNamingService.Object,
                mockScanQueue.Object);

            // Get the private method using reflection
            var method = typeof(LibraryController).GetMethod("ComputeAudiobookBaseDirectoryFromPattern",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            var result = (string)method.Invoke(controller, new object[] { audiobook, rootPath, fileNamingPattern });

            // Assert
            var expected = Path.Join("/server/mnt/drive/Audiobooks", "Stephen Graham Jones/The Buffalo Hunter Hunter");
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ComputeAudiobookBaseDirectoryFromPattern_SeriesBook_ReturnsCorrectPath()
        {
            // Arrange
            var audiobook = new Audiobook
            {
                Title = "The Gunslinger",
                Authors = new List<string> { "Stephen King" },
                PublishYear = "1982",
                Series = "The Dark Tower",
                SeriesNumber = "1"
            };

            var rootPath = "/server/mnt/drive/Audiobooks";
            var fileNamingPattern = "{Author}/{Series}/{Title}"; // Default pattern

            // Mock dependencies
            var mockRepo = new Mock<IAudiobookRepository>();
            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();
            var mockScanQueue = new Mock<IScanQueueService>();

            var mockFileNamingService = new Mock<IFileNamingService>();
            mockFileNamingService
                .Setup(x => x.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool sanitize) =>
                {
                    // For series book, the method derives pattern from fileNamingPattern by removing file-specific tokens
                    // Input: "{Author}/{Series}/{Title}"
                    // Output after processing: "{Author}/{Series}/{Title}" (DiskNumber and ChapterNumber removed)
                    if (pattern == "{Author}/{Series}/{Title}")
                    {
                        return "Stephen King/The Dark Tower/The Gunslinger";
                    }
                    return pattern;
                });

            var scopeFactory = new Mock<IServiceScopeFactory>();

            // Create controller instance
            var controller = new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory.Object,
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                mockFileNamingService.Object,
                mockScanQueue.Object);

            // Get the private method using reflection
            var method = typeof(LibraryController).GetMethod("ComputeAudiobookBaseDirectoryFromPattern",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            var result = (string)method.Invoke(controller, new object[] { audiobook, rootPath, fileNamingPattern });

            // Assert
            var expected = Path.Join("/server/mnt/drive/Audiobooks", "Stephen King/The Dark Tower/The Gunslinger");
            Assert.Equal(expected, result);
        }
    }
}
