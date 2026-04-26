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
using Listenarr.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class FileMoverFallbackTests : IDisposable
    {
        private readonly string _root;

        public FileMoverFallbackTests()
        {
            _root = Path.Join(Path.GetTempPath(), "listenarr_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Ignoring cleanup failure for '{_root}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Ignoring cleanup failure for '{_root}': {ex.Message}");
            }
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

            var mover = new FileMover(new NullLogger<FileMover>());

            var result = await mover.MoveDirectoryAsync(source, dest);

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

            var mover = new FileMover(new NullLogger<FileMover>());
            var ok = await mover.MoveFileAsync(sourceFile, destFile);

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

            var runner = new RecordingProcessRunner();
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 1,
                    RobocopyTimeoutMs = 1000,
                }));

            var source = Path.Join(_root, "missing-dir");
            var dest = Path.Join(_root, "dest-dir");

            var ok = await mover.MoveDirectoryAsync(source, dest);

            Assert.True(ok);
            Assert.NotNull(runner.LastStartInfo);
            Assert.Equal("robocopy", runner.LastStartInfo!.FileName);
            Assert.True(string.IsNullOrEmpty(runner.LastStartInfo.Arguments));
            Assert.Equal(source, runner.LastStartInfo.ArgumentList[0]);
            Assert.Equal(dest, runner.LastStartInfo.ArgumentList[1]);
            Assert.Contains("/MOVE", runner.LastStartInfo.ArgumentList);
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

            var runner = new RecordingProcessRunner();
            var mover = new FileMover(
                new NullLogger<FileMover>(),
                runner,
                Options.Create(new FileMoverOptions
                {
                    EnableRobocopy = true,
                    MaxRetries = 1,
                    RobocopyTimeoutMs = 1000,
                }));

            var sourceFile = Path.Join(_root, "missing-file.mp3");
            var destFile = Path.Join(_root, "dest", "missing-file.mp3");

            var ok = await mover.MoveFileAsync(sourceFile, destFile);

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
    }
}
