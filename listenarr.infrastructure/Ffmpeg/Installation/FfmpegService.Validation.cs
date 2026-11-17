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
    /// modification time or (on Unix) mode changes; the mode is part of the key because chmod,
    /// the execute-bit repair included, changes it without touching the modification time. A
    /// timeout, or a failure to run the process at all, says nothing lasting about the binary, so
    /// it is cached for <see cref="RetryableFailureCacheDuration"/> only and then checked again;
    /// when the same failure comes back it is logged at Debug rather than warned about again.
    /// </summary>
    public partial class FfmpegService
    {
        internal const int VersionCheckTimeoutMs = 5000;
        internal const string VersionLinePrefix = "ffprobe version";
        internal static readonly TimeSpan RetryableFailureCacheDuration = TimeSpan.FromSeconds(60);

        private readonly ConcurrentDictionary<string, FfprobeValidation> _validationCache = new(StringComparer.Ordinal);

        /// <summary>
        /// The outcome of one version check: the failure reason, or the version the binary
        /// reported. <c>RetryAfter</c> is set only for a failure that is worth checking again.
        /// </summary>
        private sealed record FfprobeValidation(long Length, DateTime LastWriteTimeUtc, UnixFileMode? Mode, string? Failure, string? Version, DateTimeOffset? RetryAfter = null);

        private static FfprobeValidation Uninspectable(string failure) => new(0, default, null, failure, null);

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

            var failure = (await ValidateFfprobeAsync(path)).Failure;
            return failure == null ? (path, null) : (null, failure);
        }

        /// <summary>
        /// Check the binary at <paramref name="path"/>. <see cref="FfprobeValidation.Failure"/> is
        /// null when it is usable, else the reason it is not.
        /// </summary>
        private async Task<FfprobeValidation> ValidateFfprobeAsync(string path)
        {
            var fullPath = Path.GetFullPath(path);
            FileInfo file;
            UnixFileMode? mode;
            try
            {
                file = new FileInfo(fullPath);
                if (!file.Exists)
                {
                    return Uninspectable("the binary does not exist");
                }

                mode = OperatingSystem.IsWindows() ? null : file.UnixFileMode;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Uninspectable($"the binary could not be inspected: {ex.Message}");
            }

            var previous = _validationCache.GetValueOrDefault(fullPath);
            if (previous != null
                && previous.Length == file.Length
                && previous.LastWriteTimeUtc == file.LastWriteTimeUtc
                && previous.Mode == mode
                && (previous.RetryAfter == null || _timeProvider.GetUtcNow() < previous.RetryAfter))
            {
                return previous;
            }

            var (failure, version, retryable) = await RunVersionCheckAsync(fullPath);
            var retryAfter = retryable ? _timeProvider.GetUtcNow() + RetryableFailureCacheDuration : (DateTimeOffset?)null;
            var validation = new FfprobeValidation(file.Length, file.LastWriteTimeUtc, mode, failure, version, retryAfter);
            _validationCache[fullPath] = validation;
            if (failure != null && previous?.RetryAfter != null && previous.Failure == failure)
            {
                _logger.LogDebug("ffprobe at {Path} is still unusable: {Reason}", fullPath, LogRedaction.SanitizeText(failure));
            }
            else if (failure != null)
            {
                _logger.LogWarning("ffprobe at {Path} is unusable: {Reason}", fullPath, LogRedaction.SanitizeText(failure));
            }
            else
            {
                _logger.LogInformation("ffprobe at {Path} reports version {Version}", fullPath, LogRedaction.SanitizeText(version));
            }

            return validation;
        }

        /// <summary>
        /// Run <c>-version</c> once. <c>Retryable</c> marks a failure that came from the run rather
        /// than from the binary's answer: a timeout, an exception from the runner, or exit -1 with
        /// no output, which is how <c>SystemProcessRunner</c> reports a process it could not start.
        /// </summary>
        private async Task<(string? Failure, string? Version, bool Retryable)> RunVersionCheckAsync(string fullPath)
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
                return ($"-version could not be run: {ex.Message}", null, true);
            }

            if (result.TimedOut)
            {
                return ($"-version did not finish within {VersionCheckTimeoutMs / 1000} s", null, true);
            }

            if (result.ExitCode != 0)
            {
                var notStarted = result.ExitCode == -1 && string.IsNullOrEmpty(result.Stdout);
                return ($"-version exited with code {result.ExitCode}", null, notStarted);
            }

            var firstLine = (result.Stdout ?? string.Empty).Split('\n', 2)[0].Trim();
            if (!firstLine.StartsWith(VersionLinePrefix, StringComparison.Ordinal))
            {
                return ($"-version output does not start with \"{VersionLinePrefix}\"", null, false);
            }

            // "ffprobe version 9.0.2-Openur Copyright (c) ..." reports "9.0.2-Openur".
            var version = firstLine[VersionLinePrefix.Length..]
                .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return (null, version, false);
        }
    }
}
