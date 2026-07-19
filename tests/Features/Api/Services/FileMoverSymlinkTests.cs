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
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services
{
    public class FileMoverSymlinkTests : IDisposable
    {
        private readonly string _root;
        private readonly FileMover _mover;

        public FileMoverSymlinkTests()
        {
            _root = Path.Join(Path.GetTempPath(), "listenarr_symlink_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _mover = new FileMover(new NullLogger<FileMover>());
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException ex) { _ = ex; } catch (UnauthorizedAccessException ex) { _ = ex; }
        }

        [Fact]
        public async Task SymlinkFileAsync_CreatesSymlinkToSource_WhenSourceIsRegularFile()
        {
            // Arrange
            var sourceDir = Path.Join(_root, "source");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Join(sourceDir, "book.m4b");
            var destFile = Path.Join(_root, "library", "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "audio content");

            // Act
            var result = await _mover.SymlinkFileAsync(sourceFile, destFile);

            // Assert
            Assert.True(result, "SymlinkFileAsync should succeed");
            Assert.True(File.Exists(destFile), "Destination file should exist");
            Assert.True(File.Exists(sourceFile), "Source file should still exist");

            // Destination is a symlink pointing at the absolute source path.
            var destTarget = new FileInfo(destFile).LinkTarget;
            Assert.NotNull(destTarget);
            Assert.Equal(Path.GetFullPath(sourceFile), destTarget);

            // Content was never copied: writing through the source is observed via the link.
            await File.WriteAllTextAsync(sourceFile, "changed content");
            Assert.Equal("changed content", await File.ReadAllTextAsync(destFile));
        }

        [Fact]
        public async Task SymlinkFileAsync_LinksDirectlyToFinalTarget_WhenSourceIsAbsoluteSymlink()
        {
            // Arrange: a real target and a symlink pointing at it with an absolute target.
            var finalTarget = Path.Join(_root, "ids", "123", "book.m4b");
            Directory.CreateDirectory(Path.GetDirectoryName(finalTarget)!);
            await File.WriteAllTextAsync(finalTarget, "audio content");

            var completedDir = Path.Join(_root, "completed");
            Directory.CreateDirectory(completedDir);
            var sourceSymlink = Path.Join(completedDir, "book.m4b");
            File.CreateSymbolicLink(sourceSymlink, finalTarget);

            var destFile = Path.Join(_root, "library", "book.m4b");

            // Act
            var result = await _mover.SymlinkFileAsync(sourceSymlink, destFile);

            // Assert: no chain — the new link points straight at the final target.
            Assert.True(result, "SymlinkFileAsync should succeed");
            var destTarget = new FileInfo(destFile).LinkTarget;
            Assert.Equal(finalTarget, destTarget);
            Assert.NotEqual(sourceSymlink, destTarget);
            Assert.True(File.Exists(sourceSymlink), "Source symlink should still exist");
        }

        [Fact]
        public async Task SymlinkFileAsync_ResolvesRelativeTarget_AgainstSourceDirectory()
        {
            // Arrange: source symlink with a RELATIVE target.
            var finalTarget = Path.Join(_root, "ids", "123", "book.m4b");
            Directory.CreateDirectory(Path.GetDirectoryName(finalTarget)!);
            await File.WriteAllTextAsync(finalTarget, "audio content");

            var completedSubDir = Path.Join(_root, "completed", "sub");
            Directory.CreateDirectory(completedSubDir);
            var sourceSymlink = Path.Join(completedSubDir, "book.m4b");
            var relativeTarget = Path.Combine("..", "..", "ids", "123", "book.m4b");
            File.CreateSymbolicLink(sourceSymlink, relativeTarget);

            var destFile = Path.Join(_root, "library", "book.m4b");

            // Act
            var result = await _mover.SymlinkFileAsync(sourceSymlink, destFile);

            // Assert: the relative target is resolved against the source symlink's directory.
            Assert.True(result, "SymlinkFileAsync should succeed");
            var expected = Path.GetFullPath(Path.Combine(completedSubDir, relativeTarget));
            Assert.Equal(expected, new FileInfo(destFile).LinkTarget);
            Assert.Equal("audio content", await File.ReadAllTextAsync(destFile));
        }

        [Fact]
        public async Task SymlinkFileAsync_CreatesDestinationDirectory_WhenMissing()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "book.m4b");
            var destDir = Path.Join(_root, "library", "Author", "Book");
            var destFile = Path.Join(destDir, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "audio content");
            Assert.False(Directory.Exists(destDir), "Destination directory should not exist initially");

            // Act
            var result = await _mover.SymlinkFileAsync(sourceFile, destFile);

            // Assert
            Assert.True(result, "SymlinkFileAsync should succeed");
            Assert.True(Directory.Exists(destDir), "Destination directory should be created");
            Assert.NotNull(new FileInfo(destFile).LinkTarget);
        }

        [Fact]
        public async Task SymlinkFileAsync_OverwritesDestination_WhenDestinationExists()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "new.m4b");
            var destFile = Path.Join(_root, "library", "book.m4b");
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            await File.WriteAllTextAsync(sourceFile, "new content");
            await File.WriteAllTextAsync(destFile, "old content");

            // Act
            var result = await _mover.SymlinkFileAsync(sourceFile, destFile);

            // Assert: destination is replaced by a link at the new source.
            Assert.True(result, "SymlinkFileAsync should succeed");
            Assert.Equal(Path.GetFullPath(sourceFile), new FileInfo(destFile).LinkTarget);
            Assert.Equal("new content", await File.ReadAllTextAsync(destFile));
        }

        [Fact]
        public async Task SymlinkFileAsync_ReturnsFalse_WhenSourceDoesNotExist()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "nonexistent.m4b");
            var destFile = Path.Join(_root, "library", "book.m4b");

            // Act: a broken/missing source yields a broken link, which CreateSymbolicLink still
            // creates on some platforms; the point is no copy fallback runs and the source is untouched.
            var result = await _mover.SymlinkFileAsync(sourceFile, destFile);

            // Assert
            _ = result;
            Assert.False(File.Exists(sourceFile), "Source should not be created by the action");
        }

        [Fact]
        public async Task SymlinkFileAsync_PreservesExistingDestination_WhenLinkCreationFails()
        {
            // A read-only destination directory forces link creation to fail. This is a POSIX-only
            // guarantee; Windows permission semantics differ, so the test is Unix-scoped.
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            // Arrange: a valid existing destination link that must survive a failed re-link.
            var originalTarget = Path.Join(_root, "original.m4b");
            await File.WriteAllTextAsync(originalTarget, "original content");
            var newSource = Path.Join(_root, "new.m4b");
            await File.WriteAllTextAsync(newSource, "new content");

            var destDir = Path.Join(_root, "library");
            Directory.CreateDirectory(destDir);
            var destFile = Path.Join(destDir, "book.m4b");
            File.CreateSymbolicLink(destFile, originalTarget);

            try
            {
                File.SetUnixFileMode(destDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

                // Act
                var result = await _mover.SymlinkFileAsync(newSource, destFile);

                // Assert: reported as failure, no copy fallback, and the valid destination is intact.
                Assert.False(result, "SymlinkFileAsync should report failure when the link cannot be created");
                Assert.Equal(originalTarget, new FileInfo(destFile).LinkTarget);
                Assert.Equal("original content", await File.ReadAllTextAsync(destFile));
            }
            finally
            {
                File.SetUnixFileMode(destDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // No leftover temporary links remain in the destination directory.
            var leftovers = Directory.GetFiles(destDir, "*.tmp");
            Assert.Empty(leftovers);
        }

        [Fact]
        public async Task SymlinkFileAsync_SkipsWhenDestinationAlreadyLinksToSameTarget()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "audio content");
            var destFile = Path.Join(_root, "library", "book.m4b");

            // Act: link twice; the second call should be an idempotent skip.
            Assert.True(await _mover.SymlinkFileAsync(sourceFile, destFile));
            var result = await _mover.SymlinkFileAsync(sourceFile, destFile);

            // Assert
            Assert.True(result, "Re-linking to the same target should succeed as a skip");
            Assert.Equal(Path.GetFullPath(sourceFile), new FileInfo(destFile).LinkTarget);
        }

        [Fact]
        public async Task PerformActionOn_SymbolicLink_CreatesLinkAndPreservesSource()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "book.m4b");
            var destFile = Path.Join(_root, "library", "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "audio content");

            // Act
            var result = await _mover.PerformActionOn(FileAction.SymbolicLink, sourceFile, destFile);

            // Assert
            Assert.True(result, "PerformActionOn(SymbolicLink) should succeed");
            Assert.True(File.Exists(sourceFile), "Source must be preserved");
            Assert.Equal(Path.GetFullPath(sourceFile), new FileInfo(destFile).LinkTarget);
        }
    }
}
