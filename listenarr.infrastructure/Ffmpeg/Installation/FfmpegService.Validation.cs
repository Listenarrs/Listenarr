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
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// Runs <c>ffprobe -version</c> on a resolved binary before it is used. A binary is accepted
    /// only when the check exits 0 within <see cref="VersionCheckTimeoutMs"/> and its first output
    /// line starts with <c>ffprobe version</c>, so a truncated download, another platform's build
    /// or an unrelated file at the expected path is reported as unavailable instead of failing
    /// every probe. The result is cached per path and invalidated when the file's length,
    /// modification time or (on Unix) mode changes; the mode is part of the key because the
    /// execute-bit repair changes it without touching the modification time.
    /// </summary>
    public partial class FfmpegService
    {
        internal const int VersionCheckTimeoutMs = 5000;
        internal const string VersionLinePrefix = "ffprobe version";

        private readonly ConcurrentDictionary<string, FfprobeValidation> _validationCache = new(StringComparer.Ordinal);

        private sealed record FfprobeValidation(long Length, DateTime LastWriteTimeUtc, UnixFileMode? Mode, string? Failure);

        /// <summary>
        /// The binary <see cref="ResolveFfprobePath"/> picks, if it passes the version check. When
        /// a packaged binary exists but fails, no other binary is tried: a copy in the configured
        /// ffmpeg directory is not a fallback for a broken package.
        /// </summary>
        private async Task<(string? Path, string? Failure)> ResolveValidatedFfprobePathAsync()
        {
            var path = ResolveFfprobePath();
            if (path == null)
            {
                return (null, null);
            }

            var failure = await ValidateFfprobeAsync(path);
            return failure == null ? (path, null) : (null, failure);
        }

        /// <summary>Null when the binary at <paramref name="path"/> is usable, else the reason it is not.</summary>
        private async Task<string?> ValidateFfprobeAsync(string path)
        {
            var fullPath = Path.GetFullPath(path);
            FileInfo file;
            UnixFileMode? mode;
            try
            {
                file = new FileInfo(fullPath);
                if (!file.Exists)
                {
                    return "the binary does not exist";
                }

                mode = OperatingSystem.IsWindows() ? null : file.UnixFileMode;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"the binary could not be inspected: {ex.Message}";
            }

            if (_validationCache.TryGetValue(fullPath, out var cached)
                && cached.Length == file.Length
                && cached.LastWriteTimeUtc == file.LastWriteTimeUtc
                && cached.Mode == mode)
            {
                return cached.Failure;
            }

            var failure = await RunVersionCheckAsync(fullPath);
            _validationCache[fullPath] = new FfprobeValidation(file.Length, file.LastWriteTimeUtc, mode, failure);
            if (failure != null)
            {
                _logger.LogWarning("ffprobe at {Path} is unusable: {Reason}", fullPath, LogRedaction.SanitizeText(failure));
            }

            return failure;
        }

        private async Task<string?> RunVersionCheckAsync(string fullPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-version");

            ProcessResult result;
            try
            {
                result = await _processRunner.RunAsync(startInfo, VersionCheckTimeoutMs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return $"-version could not be run: {ex.Message}";
            }

            if (result.TimedOut)
            {
                return $"-version did not finish within {VersionCheckTimeoutMs / 1000} s";
            }

            if (result.ExitCode != 0)
            {
                return $"-version exited with code {result.ExitCode}";
            }

            var firstLine = (result.Stdout ?? string.Empty).Split('\n', 2)[0].Trim();
            return firstLine.StartsWith(VersionLinePrefix, StringComparison.Ordinal)
                ? null
                : $"-version output does not start with \"{VersionLinePrefix}\"";
        }
    }
}
