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
using Listenarr.Application.Audiobooks.Verification.Contracts;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Ffmpeg.Sampling
{
    /// <summary>
    /// Clips verification sample windows to 16 kHz mono WAV via the bundled
    /// ffmpeg (ADR-0001). The closing window uses ffmpeg's -sseof (seek from
    /// end-of-file) so no duration probe is needed.
    /// </summary>
    public class AudioSampleExtractor : IAudioSampleExtractor
    {
        private const int ClipTimeoutMs = 2 * 60 * 1000;

        private readonly IFfmpegService _ffmpegService;
        private readonly IProcessRunner _processRunner;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AudioSampleExtractor> _logger;

        public AudioSampleExtractor(
            IFfmpegService ffmpegService,
            IProcessRunner processRunner,
            IConfigurationService configurationService,
            ILogger<AudioSampleExtractor> logger)
        {
            _ffmpegService = ffmpegService;
            _processRunner = processRunner;
            _configurationService = configurationService;
            _logger = logger;
        }

        public async Task<AudioSampleSet> ExtractAsync(string firstFilePath, string? lastFilePath, AudioSampleStrategy strategy, CancellationToken cancellationToken = default)
        {
            var ffmpegPath = await _ffmpegService.EnsureFfmpegInstalledAsync();
            if (ffmpegPath == null)
            {
                _logger.LogWarning("Cannot extract verification clips: ffmpeg binary unavailable");
                return new AudioSampleSet();
            }

            var tempDir = Path.Join(Path.GetTempPath(), "listenarr-verification");
            Directory.CreateDirectory(tempDir);

            // Same low-priority treatment as the whisper pass: clipping is short,
            // but a library walk runs it back-to-back for hours.
            var settings = await _configurationService.GetApplicationSettingsAsync();
            var priorityClass = (settings?.VerificationLowCpuPriority ?? true)
                ? ProcessPriorityClass.Idle
                : (ProcessPriorityClass?)null;

            string? opening = null, closing = null;

            if (strategy.OpeningSeconds > 0 && File.Exists(firstFilePath))
            {
                opening = await ClipAsync(ffmpegPath, firstFilePath, tempDir, fromEnd: false, strategy.OpeningSeconds, priorityClass, cancellationToken);
            }

            var closingSource = lastFilePath ?? firstFilePath;
            if (strategy.ClosingSeconds > 0 && File.Exists(closingSource))
            {
                closing = await ClipAsync(ffmpegPath, closingSource, tempDir, fromEnd: true, strategy.ClosingSeconds, priorityClass, cancellationToken);
            }

            return new AudioSampleSet(TryDeleteClip) { OpeningClipPath = opening, ClosingClipPath = closing };
        }

        private async Task<string?> ClipAsync(string ffmpegPath, string sourcePath, string tempDir, bool fromEnd, int seconds, ProcessPriorityClass? priorityClass, CancellationToken ct)
        {
            var clipPath = Path.Join(tempDir, $"{Guid.NewGuid():N}.wav");
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-hide_banner");
                startInfo.ArgumentList.Add("-loglevel");
                startInfo.ArgumentList.Add("error");
                if (fromEnd)
                {
                    // Seek relative to end-of-file; on inputs shorter than the
                    // window ffmpeg just starts at 0.
                    startInfo.ArgumentList.Add("-sseof");
                    startInfo.ArgumentList.Add($"-{seconds}");
                }
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(sourcePath);
                if (!fromEnd)
                {
                    startInfo.ArgumentList.Add("-t");
                    startInfo.ArgumentList.Add(seconds.ToString());
                }
                // whisper.cpp expects 16 kHz mono PCM.
                startInfo.ArgumentList.Add("-vn");
                startInfo.ArgumentList.Add("-ac");
                startInfo.ArgumentList.Add("1");
                startInfo.ArgumentList.Add("-ar");
                startInfo.ArgumentList.Add("16000");
                startInfo.ArgumentList.Add("-c:a");
                startInfo.ArgumentList.Add("pcm_s16le");
                startInfo.ArgumentList.Add("-y");
                startInfo.ArgumentList.Add(clipPath);

                var result = await _processRunner.RunAsync(startInfo, ClipTimeoutMs, ct, priorityClass);
                if (result.ExitCode != 0 || result.TimedOut || !File.Exists(clipPath))
                {
                    _logger.LogWarning(
                        "ffmpeg clip failed for {Source} ({Window}): exit {Code}, timedOut {TimedOut}",
                        sourcePath, fromEnd ? "closing" : "opening", result.ExitCode, result.TimedOut);
                    TryDelete(clipPath);
                    return null;
                }

                return clipPath;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(clipPath);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "ffmpeg clip failed for {Source}", sourcePath);
                TryDelete(clipPath);
                return null;
            }
        }

        private static void TryDeleteClip(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            TryDelete(path);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* best-effort temp cleanup */ }
        }
    }
}
