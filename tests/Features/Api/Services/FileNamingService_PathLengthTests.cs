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
using System.Runtime.InteropServices;

namespace Listenarr.Tests.Features.Api.Services
{
    /// <summary>
    /// Tests for FileNamingService Windows path length enforcement (MAX_PATH / per-component limits)
    /// </summary>
    [Trait("Category", "FileNamingService")]
    public class FileNamingService_PathLengthTests
    {
        private readonly FileNamingService _service;

        public FileNamingService_PathLengthTests()
        {
            var mockConfig = new Mock<IConfigurationService>();
            var mockLogger = new Mock<ILogger<FileNamingService>>();
            _service = new FileNamingService(mockConfig.Object, mockLogger.Object);
        }

        [Fact]
        public void EnsurePathWithinLimits_ShortPath_ReturnsUnchanged()
        {
            var path = @"D:\Audiobooks\Author\Title\Book.m4b";
            var result = _service.EnsurePathWithinLimits(path);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Equal(path, result);
            }
        }

        [Fact]
        public void EnsurePathWithinLimits_PathExceeding260Chars_IsTruncated()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return; // Limit only enforced on Windows

            // Build a path well over 260 characters
            var longAuthor = new string('A', 100);
            var longTitle = new string('T', 200);
            var path = $@"D:\Audiobooks\{longAuthor}\{longTitle}\{longTitle}.m4b";

            Assert.True(path.Length > 259, $"Test path should exceed 259 chars, was {path.Length}");

            var result = _service.EnsurePathWithinLimits(path);

            Assert.True(result.Length <= 259, $"Result path should be ≤ 259 chars, was {result.Length}");
            Assert.EndsWith(".m4b", result);
        }

        [Fact]
        public void EnsurePathWithinLimits_PreservesExtension()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var longTitle = new string('T', 300);
            var path = $@"D:\Audiobooks\Author\{longTitle}.mp3";

            var result = _service.EnsurePathWithinLimits(path);

            Assert.True(result.Length <= 259);
            Assert.EndsWith(".mp3", result);
        }

        [Fact]
        public void EnsurePathWithinLimits_ComponentExceeding255Chars_IsTruncated()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            // Single component over 255 chars but total path under 260
            // Not realistic on Windows (260 total means components can't be that long with a root)
            // but test the per-component logic directly
            var longFolder = new string('F', 256);
            var path = $@"D:\{longFolder}\Book.m4b";

            var result = _service.EnsurePathWithinLimits(path);

            // Each component should be ≤ 255
            var parts = result.Substring(Path.GetPathRoot(result)!.Length)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                Assert.True(part.Length <= 255, $"Component '{part.Substring(0, Math.Min(30, part.Length))}...' is {part.Length} chars, exceeds 255");
            }
        }

        [Fact]
        public void EnsurePathWithinLimits_TruncatesLongestComponentFirst()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            // Create a path where the title folder is much longer than the author
            var shortAuthor = "Author";
            var longTitle = new string('T', 200);
            var filename = "Book.m4b";
            var path = $@"D:\Audiobooks\{shortAuthor}\{longTitle}\{filename}";

            var result = _service.EnsurePathWithinLimits(path);

            Assert.True(result.Length <= 259);
            // Author should be preserved since it's short; the long title should be truncated
            Assert.Contains(shortAuthor, result);
            Assert.EndsWith(".m4b", result);
        }

        [Fact]
        public void EnsurePathWithinLimits_EmptyOrNull_ReturnsAsIs()
        {
            Assert.Equal("", _service.EnsurePathWithinLimits(""));
            Assert.Null(_service.EnsurePathWithinLimits(null!));
        }

        [Fact]
        public void EnsurePathWithinLimits_ExactlyAtLimit_ReturnsUnchanged()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            // Build a path that's exactly 259 chars
            var root = @"D:\";
            var remaining = 259 - root.Length - ".m4b".Length - 1; // -1 for separator before filename
            var folder = new string('X', remaining / 2);
            var file = new string('Y', remaining - folder.Length);
            var path = $@"{root}{folder}\{file}.m4b";

            // Verify our test setup
            Assert.Equal(259, path.Length);

            var result = _service.EnsurePathWithinLimits(path);
            Assert.Equal(path, result);
        }
    }
}
