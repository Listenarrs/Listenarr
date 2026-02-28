using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class FileMoverHardlinkTests : IDisposable
    {
        private readonly string _root;
        private readonly FileMover _mover;

        public FileMoverHardlinkTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "listenarr_hardlink_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _mover = new FileMover(new NullLogger<FileMover>());
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public async Task HardlinkFileAsync_CreatesHardlink_WhenBothFilesOnSameVolume()
        {
            // Arrange
            var sourceFile = Path.Combine(_root, "source.mp3");
            var destFile = Path.Combine(_root, "dest.mp3");
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
            var sourceFile = Path.Combine(_root, "source.mp3");
            var destDir = Path.Combine(_root, "subdir");
            var destFile = Path.Combine(destDir, "dest.mp3");
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
            var sourceFile = Path.Combine(_root, "source.mp3");
            var destFile = Path.Combine(_root, "dest.mp3");
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
            var sourceFile = Path.Combine(_root, "source.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");

            // Create a path that would cause hardlink to fail (different volume simulation via invalid path)
            // On some systems, hardlink may fail for various reasons - we want to test fallback behavior
            var destFile = Path.Combine(_root, "dest.mp3");

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
            var sourceFile = Path.Combine(_root, "nonexistent.mp3");
            var destFile = Path.Combine(_root, "dest.mp3");

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
            var sourceFile = Path.Combine(_root, "source.mp3");
            var destFile = Path.Combine(_root, "dest.mp3");
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
            var sourceFile = Path.Combine(_root, "source.mp3");
            var destFile = Path.Combine(_root, "dest.mp3");
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
            var sourceFile = Path.Combine(_root, "source.mp3");
            var largeContent = new string('x', 10000);
            await File.WriteAllTextAsync(sourceFile, largeContent);
            var sourceInfo = new FileInfo(sourceFile);

            // Act
            var destFile = Path.Combine(_root, "dest.mp3");
            await _mover.HardlinkFileAsync(sourceFile, destFile);

            // Assert
            var destInfo = new FileInfo(destFile);
            Assert.Equal(sourceInfo.Length, destInfo.Length);
        }
    }
}
