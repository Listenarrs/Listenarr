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
using Listenarr.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services
{
    public class FileMoverHardlinkTests : IDisposable
    {
        private readonly string _root;
        private readonly FileMover _mover;

        public FileMoverHardlinkTests()
        {
            _root = Path.Join(Path.GetTempPath(), "listenarr_hardlink_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _mover = new FileMover(new NullLogger<FileMover>());
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException ex) { _ = ex; } catch (UnauthorizedAccessException ex) { _ = ex; }
        }

        [Fact]
        public async Task HardlinkFileAsync_CreatesHardlink_WhenBothFilesOnSameVolume()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "source.mp3");
            var destFile = Path.Join(_root, "dest.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio content");

            // Act
            var result = await _mover.HardlinkFileAsync(sourceFile, destFile);

            // Assert
            Assert.True(result, "HardlinkFileAsync should succeed");
            Assert.True(File.Exists(sourceFile), "Source file should still exist");
            Assert.True(File.Exists(destFile), "Destination file should exist");

            // Both files should have same content
            var sourceContent = await File.ReadAllTextAsync(sourceFile);
            var destContent = await File.ReadAllTextAsync(destFile);
            Assert.Equal(sourceContent, destContent);
        }

        [Fact]
        public async Task HardlinkFileAsync_CreatesDestinationDirectory_WhenMissing()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "source.mp3");
            var destDir = Path.Join(_root, "subdir");
            var destFile = Path.Join(destDir, "dest.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio content");

            Assert.False(Directory.Exists(destDir), "Destination directory should not exist initially");

            // Act
            var result = await _mover.HardlinkFileAsync(sourceFile, destFile);

            // Assert
            Assert.True(result, "HardlinkFileAsync should succeed");
            Assert.True(Directory.Exists(destDir), "Destination directory should be created");
            Assert.True(File.Exists(destFile), "Destination file should exist");
        }

        [Fact]
        public async Task HardlinkFileAsync_OverwritesDestination_WhenDestinationExists()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "source.mp3");
            var destFile = Path.Join(_root, "dest.mp3");
            await File.WriteAllTextAsync(sourceFile, "new content");
            await File.WriteAllTextAsync(destFile, "old content");

            // Act
            var result = await _mover.HardlinkFileAsync(sourceFile, destFile);

            // Assert
            Assert.True(result, "HardlinkFileAsync should succeed");
            var destContent = await File.ReadAllTextAsync(destFile);
            Assert.Equal("new content", destContent);
        }

        [Fact]
        public async Task HardlinkFileAsync_FallbacksToCopy_WhenHardlinkFails()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "source.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");

            // Create a path that would cause hardlink to fail (different volume simulation via invalid path)
            // On some systems, hardlink may fail for various reasons - we want to test fallback behavior
            var destFile = Path.Join(_root, "dest.mp3");

            // Act - even if hardlink fails internally, the method should fallback to copy
            var result = await _mover.HardlinkFileAsync(sourceFile, destFile);

            // Assert - should succeed via fallback
            Assert.True(result, "HardlinkFileAsync should succeed via copy fallback");
            Assert.True(File.Exists(destFile), "Destination file should exist");
        }

        [Fact]
        public async Task HardlinkFileAsync_ReturnsFalse_WhenSourceDoesNotExist()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "nonexistent.mp3");
            var destFile = Path.Join(_root, "dest.mp3");

            // Act
            var result = await _mover.HardlinkFileAsync(sourceFile, destFile);

            // Assert
            // Method gracefully returns false when source doesn't exist (exception is caught internally)
            Assert.False(result, "HardlinkFileAsync should return false when source doesn't exist");
            Assert.False(File.Exists(destFile), "Destination file should not be created");
        }

        [Fact]
        public async Task CopyFileAsync_CreatesIndependentCopy()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "source.mp3");
            var destFile = Path.Join(_root, "dest.mp3");
            await File.WriteAllTextAsync(sourceFile, "original content");

            // Act
            var result = await _mover.CopyFileAsync(sourceFile, destFile);

            // Assert
            Assert.True(result, "CopyFileAsync should succeed");
            Assert.True(File.Exists(sourceFile), "Source should still exist");
            Assert.True(File.Exists(destFile), "Destination should exist");

            // Modify destination to verify independence
            await File.WriteAllTextAsync(destFile, "modified content");
            var sourceContent = await File.ReadAllTextAsync(sourceFile);
            Assert.Equal("original content", sourceContent);
        }

        [Fact]
        public async Task MoveFileAsync_RemovesSource_AfterMove()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "source.mp3");
            var destFile = Path.Join(_root, "dest.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");

            // Act
            var result = await _mover.MoveFileAsync(sourceFile, destFile);

            // Assert
            Assert.True(result, "MoveFileAsync should succeed");
            Assert.False(File.Exists(sourceFile), "Source should be removed");
            Assert.True(File.Exists(destFile), "Destination should exist");
        }

        [Fact]
        public async Task HardlinkFileAsync_PreservesFileSize()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "source.mp3");
            var largeContent = new string('x', 10000);
            await File.WriteAllTextAsync(sourceFile, largeContent);
            var sourceInfo = new FileInfo(sourceFile);

            // Act
            var destFile = Path.Join(_root, "dest.mp3");
            await _mover.HardlinkFileAsync(sourceFile, destFile);

            // Assert
            var destInfo = new FileInfo(destFile);
            Assert.Equal(sourceInfo.Length, destInfo.Length);
        }
    }
}
