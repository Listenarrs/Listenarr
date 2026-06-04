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
using System.Diagnostics;
using System.Runtime.InteropServices;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models.Enumerations;
using Listenarr.Infrastructure.FileSystem;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem
{
    public class FileMoverTests : BaseTests
    {
        private readonly string _root;
        private IFileMover _mover;

        public FileMoverTests()
        {
            _root = FileService.GetTempDirectory("root");
            _mover = _provider.GetRequiredService<IFileMover>();
        }

        [Fact]
        public async Task MoveDirectoryAsync_WhenDestinationExists_UsesCopyAndDeleteFallback()
        {
            var source = Path.Join(_root, "sourceDir");
            var dest = Path.Join(_root, "destDir");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(dest); // cause Directory.Move to throw (destination exists)

            var fileInSource = Path.Join(source, "track1.mp3");
            await File.WriteAllTextAsync(fileInSource, "dummy");

            var result = await _mover.MoveDirectoryAsync(source, dest);

            Assert.True(result, "MoveDirectoryAsync should succeed via fallback");
            // Source should be removed
            Assert.False(Directory.Exists(source));
            // Destination should contain the file
            var copied = Path.Join(dest, "track1.mp3");
            Assert.True(File.Exists(copied));
        }

        [Fact]
        public async Task MoveFileAsync_MovesFileSuccessfully()
        {
            var sourceFile = Path.Join(_root, "a.mp3");
            var destFile = Path.Join(_root, "b.mp3");
            await File.WriteAllTextAsync(sourceFile, "content");

            var ok = await _mover.PerformActionOn(FileAction.Move, sourceFile, destFile);

            Assert.True(ok);
            Assert.False(File.Exists(sourceFile));
            Assert.True(File.Exists(destFile));
        }

        [Fact]
        public async Task MoveDirectoryAsync_RobocopyFallback_UsesArgumentList()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            _services.AddSingleton<IProcessRunner>(new RecordingProcessRunner());
            _services.AddSingleton(new FileMoverOptions
            {
                EnableRobocopy = true,
                MaxRetries = 1,
                RobocopyTimeoutMs = 1000,
            });
            Init();

            var runner = (RecordingProcessRunner)_provider.GetRequiredService<IProcessRunner>();
            _mover = _provider.GetRequiredService<IFileMover>();

            var source = Path.Join(_root, "missing-dir");
            var dest = Path.Join(_root, "dest-dir");

            var ok = await _mover.MoveDirectoryAsync(source, dest);

            Assert.True(ok);
            Assert.NotNull(runner.LastStartInfo);
            Assert.Equal("robocopy", runner.LastStartInfo!.FileName);
            Assert.True(string.IsNullOrEmpty(runner.LastStartInfo.Arguments));
            Assert.Equal(source, runner.LastStartInfo.ArgumentList[0]);
            Assert.Equal(dest, runner.LastStartInfo.ArgumentList[1]);
            Assert.Contains("/MOV", runner.LastStartInfo.ArgumentList);
            Assert.All(runner.LastStartInfo.ArgumentList, argument =>
            {
                Assert.False(argument.StartsWith("\"", StringComparison.Ordinal));
                Assert.False(argument.EndsWith("\"", StringComparison.Ordinal));
            });
        }

        [Fact]
        public async Task MoveFileAsync_RobocopyFallback_UsesArgumentList()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            _services.AddSingleton<IProcessRunner>(new RecordingProcessRunner());
            _services.AddSingleton(new FileMoverOptions
            {
                EnableRobocopy = true,
                MaxRetries = 1,
                RobocopyTimeoutMs = 1000,
            });
            Init();

            var runner = (RecordingProcessRunner)_provider.GetRequiredService<IProcessRunner>();
            _mover = _provider.GetRequiredService<IFileMover>();

            var sourceFile = Path.Join(_root, "missing-file.mp3");
            var destFile = Path.Join(_root, "dest", "missing-file.mp3");

            var ok = await _mover.PerformActionOn(FileAction.Move, sourceFile, destFile);

            Assert.True(ok);
            Assert.NotNull(runner.LastStartInfo);
            Assert.Equal("robocopy", runner.LastStartInfo!.FileName);
            Assert.True(string.IsNullOrEmpty(runner.LastStartInfo.Arguments));
            Assert.Equal(Path.GetDirectoryName(sourceFile) ?? string.Empty, runner.LastStartInfo.ArgumentList[0]);
            Assert.Equal(Path.GetDirectoryName(destFile) ?? string.Empty, runner.LastStartInfo.ArgumentList[1]);
            Assert.Equal(Path.GetFileName(sourceFile), runner.LastStartInfo.ArgumentList[2]);
            Assert.Contains("/MOV", runner.LastStartInfo.ArgumentList);
        }

        private sealed class RecordingProcessRunner : IProcessRunner
        {
            public ProcessStartInfo? LastStartInfo { get; private set; }

            public Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = 60000, System.Threading.CancellationToken cancellationToken = default)
            {
                LastStartInfo = startInfo;
                return Task.FromResult(new ProcessResult(1, string.Empty, string.Empty, false));
            }

            public Process StartProcess(ProcessStartInfo startInfo) => throw new NotSupportedException();

            public IDisposable RegisterTransientSensitive(IEnumerable<string> values) => new NoopDisposable();

            private sealed class NoopDisposable : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }

        [Fact]
        public async Task HardlinkFileAsync_CreatesHardlink_WhenBothFilesOnSameVolume()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "source.mp3");
            var destFile = Path.Join(_root, "dest.mp3");
            await File.WriteAllTextAsync(sourceFile, "audio content");

            // Act
            var result = await _mover.PerformActionOn(FileAction.HardlinkCopy, sourceFile, destFile);

            // Assert
            Assert.True(result, "HardlinkFileAsync should succeed");
            Assert.True(File.Exists(sourceFile), "Source file should still exist");
            Assert.True(File.Exists(destFile), "Destination file should exist");

            // Modify source
            await File.WriteAllTextAsync(sourceFile, "updated content");

            // Check destination reflect those changes
            var sourceContent = await File.ReadAllTextAsync(sourceFile);
            var destContent = await File.ReadAllTextAsync(destFile);
            Assert.Equal("updated content", sourceContent);
            Assert.Equal("updated content", destContent);
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
            var result = await _mover.PerformActionOn(FileAction.HardlinkCopy, sourceFile, destFile);

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
            var result = await _mover.PerformActionOn(FileAction.HardlinkCopy, sourceFile, destFile);

            // Assert
            Assert.True(result, "HardlinkFileAsync should succeed");
            var destContent = await File.ReadAllTextAsync(destFile);
            Assert.Equal("new content", destContent);
        }

        [Fact]
        public async Task HardlinkFileAsync_ReturnsFalse_WhenSourceDoesNotExist()
        {
            // Arrange
            var sourceFile = Path.Join(_root, "nonexistent.mp3");
            var destFile = Path.Join(_root, "dest.mp3");

            // Act
            var result = await _mover.PerformActionOn(FileAction.HardlinkCopy, sourceFile, destFile);

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
            var result = await _mover.PerformActionOn(FileAction.Copy, sourceFile, destFile);

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
            var result = await _mover.PerformActionOn(FileAction.Move, sourceFile, destFile);

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
            await _mover.PerformActionOn(FileAction.HardlinkCopy, sourceFile, destFile);

            // Assert
            var destInfo = new FileInfo(destFile);
            Assert.Equal(sourceInfo.Length, destInfo.Length);
        }

        [Fact]
        public async Task CopyDir_NoInfiniteLoop()
        {
            // Arrange
            var sourceDirectory = FileService.GetTempDirectory("source");
            var sourceSubDirectory = FileService.GetTempDirectory("source", "test");

            await FileService.GetFileAsync(sourceDirectory, "source.mp3");
            await FileService.GetFileAsync(sourceSubDirectory, "subsource.mp3");

            var destinationDirectory = FileService.GetTempDirectory("source", "test", "destination");

            // Act
            try
            {
                ((FileMover)_mover).CopyDirRecursive(sourceDirectory, destinationDirectory);
            }
            catch (Exception exception)
            {
                Assert.Fail(exception.Message);
            }

            // Assert
            var files = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories);
            Assert.Equal(4, files.Length);
        }

        [Fact]
        public async Task CopyDir_WorksWith_SimilarDirectory()
        {
            // Arrange
            var sourceDirectory = FileService.GetTempDirectory("source");
            var sourceSubDirectory = FileService.GetTempDirectory("source", "test");

            await FileService.GetFileAsync(sourceDirectory, "source.mp3");
            await FileService.GetFileAsync(sourceSubDirectory, "subsource.mp3");

            var destinationDirectory = FileService.GetTempDirectory("source-copy", "test", "destination");

            // Act
            try
            {
                ((FileMover)_mover).CopyDirRecursive(sourceDirectory, destinationDirectory);
            }
            catch (Exception exception)
            {
                Assert.Fail(exception.Message);
            }

            // Assert
            var files = Directory.GetFiles(destinationDirectory, "*.*", SearchOption.AllDirectories);
            Assert.Equal(2, files.Length);
        }
    }
}
