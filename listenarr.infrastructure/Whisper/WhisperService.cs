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
using System.Text.RegularExpressions;
using Listenarr.Application.Audiobooks.Verification.Contracts;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Whisper
{
    /// <summary>
    /// Shell-out wrapper around a bundled whisper.cpp CLI (ADR-0001).
    ///
    /// Packaging is deliberately NOT the FFmpeg runtime-download pattern:
    /// whisper.cpp is MIT and ships no prebuilt Linux binaries (v1.8.6 releases
    /// carry only Windows zips and Apple frameworks), so the Docker image bakes
    /// the binary + ggml model in at build time (see the whisper-builder stage
    /// in the Dockerfile). Dev machines point LISTENARR_WHISPER_BIN /
    /// LISTENARR_WHISPER_MODEL at a local build instead.
    /// </summary>
    public class WhisperService : IWhisperService
    {
        public const string BinaryPathEnvVar = "LISTENARR_WHISPER_BIN";
        public const string ModelPathEnvVar = "LISTENARR_WHISPER_MODEL";

        // base.en transcribes a 90s clip in seconds on a modern CPU, but the
        // target host may be a busy NAS — be generous before declaring a hang.
        private const int TranscribeTimeoutMs = 10 * 60 * 1000;

        private readonly IProcessRunner _processRunner;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<WhisperService> _logger;
        private readonly string _binaryPath;
        private readonly string _modelPath;

        public WhisperService(
            IApplicationPathService applicationPathService,
            IProcessRunner processRunner,
            IConfigurationService configurationService,
            ILogger<WhisperService> logger)
        {
            _processRunner = processRunner;
            _configurationService = configurationService;
            _logger = logger;

            var whisperRoot = Path.Join(applicationPathService.ToolsRootPath, "whisper");
            _binaryPath = Environment.GetEnvironmentVariable(BinaryPathEnvVar)
                          ?? Path.Join(whisperRoot, "whisper-cli");
            _modelPath = Environment.GetEnvironmentVariable(ModelPathEnvVar)
                         ?? Path.Join(whisperRoot, "ggml-base.en.bin");
        }

        public string ModelName
        {
            get
            {
                // "ggml-base.en.bin" → "base.en"; fall back to the bare file name.
                var fileName = Path.GetFileNameWithoutExtension(_modelPath);
                var match = Regex.Match(fileName, @"^ggml-(.+)$");
                return match.Success ? match.Groups[1].Value : fileName;
            }
        }

        public Task<bool> IsAvailableAsync()
        {
            var available = File.Exists(_binaryPath) && File.Exists(_modelPath);
            if (!available)
            {
                _logger.LogInformation(
                    "whisper.cpp unavailable (binary {BinaryExists} at {Binary}, model {ModelExists} at {Model})",
                    File.Exists(_binaryPath), _binaryPath, File.Exists(_modelPath), _modelPath);
            }
            return Task.FromResult(available);
        }

        public async Task<string?> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default)
        {
            if (!await IsAvailableAsync()) return null;
            if (!File.Exists(wavPath))
            {
                _logger.LogWarning("whisper transcription requested for missing clip {Path}", wavPath);
                return null;
            }

            try
            {
                // whisper.cpp saturates its threads for the whole transcription, so a
                // library walk runs it at idle priority (unless disabled in settings)
                // to yield CPU to anything else on the host.
                var settings = await _configurationService.GetApplicationSettingsAsync();
                var priorityClass = (settings?.VerificationLowCpuPriority ?? true)
                    ? ProcessPriorityClass.Idle
                    : (ProcessPriorityClass?)null;

                var stdout = await RunWhisperCliAsync(wavPath, offsetMs: null, durationMs: null, priorityClass, cancellationToken);
                if (stdout == null) return null;

                var segments = ParseSegments(stdout);
                if (segments.Count == 0)
                {
                    // No timestamped lines (unexpected output shape) — fall back to
                    // the plain collapse.
                    return NormalizeTranscriptOutput(stdout);
                }

                // Gap re-probe: whisper decodes in 30s chunks, and a chunk whose
                // remainder is music-overlaid speech can be skipped wholesale —
                // live case: 0:02–0:30 (the entire spoken credits) came back as
                // dead air while the SAME span transcribed perfectly in isolation.
                // For every silent hole big enough to hide a credits block, re-run
                // the decoder on just that span (whisper-cli --offset-t/--duration)
                // and splice the recovered text in.
                var clipSeconds = TryGetWavDurationSeconds(wavPath) ?? segments[^1].End;
                var gaps = FindSilentGaps(segments, clipSeconds);
                foreach (var gap in gaps.Take(MaxGapProbes))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var probeStdout = await RunWhisperCliAsync(
                        wavPath,
                        offsetMs: (int)(gap.Start * 1000),
                        durationMs: (int)((gap.End - gap.Start) * 1000),
                        priorityClass,
                        cancellationToken);
                    var probeText = NormalizeTranscriptOutput(probeStdout);
                    if (!string.IsNullOrWhiteSpace(probeText))
                    {
                        _logger.LogInformation(
                            "whisper gap re-probe recovered {Chars} chars from {Start:0.0}s–{End:0.0}s of {Path}",
                            probeText.Length, gap.Start, gap.End, wavPath);
                        segments.Add(new TranscriptSegment(gap.Start, gap.End, probeText));
                    }
                }

                var transcript = string.Join(" ", segments.OrderBy(s => s.Start).Select(s => s.Text)).Trim();
                return transcript.Length == 0 ? null : transcript;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "whisper transcription failed for {Path}", wavPath);
                return null;
            }
        }

        private async Task<string?> RunWhisperCliAsync(
            string wavPath, int? offsetMs, int? durationMs, ProcessPriorityClass? priorityClass, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _binaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(_modelPath);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(wavPath);
            // Deliberately NOT --no-timestamps: timestamp tokens anchor the
            // decoder, and without them base.en demonstrably skips
            // music-overlaid segments — on a live book the no-timestamps
            // decode dropped the entire spoken credits (0:09–0:30) while the
            // timestamped decode of the same clip transcribed them verbatim.
            // The segment timestamps are also what the gap re-probe needs.
            startInfo.ArgumentList.Add("--no-prints");
            startInfo.ArgumentList.Add("--language");
            startInfo.ArgumentList.Add("en");
            if (offsetMs is > 0)
            {
                startInfo.ArgumentList.Add("--offset-t");
                startInfo.ArgumentList.Add(offsetMs.Value.ToString());
            }
            if (durationMs is > 0)
            {
                startInfo.ArgumentList.Add("--duration");
                startInfo.ArgumentList.Add(durationMs.Value.ToString());
            }

            var result = await _processRunner.RunAsync(startInfo, TranscribeTimeoutMs, cancellationToken, priorityClass);
            if (result.TimedOut)
            {
                _logger.LogWarning("whisper transcription timed out for {Path}", wavPath);
                return null;
            }
            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "whisper exited with code {Code} for {Path}: {Stderr}",
                    result.ExitCode, wavPath, Truncate(result.Stderr, 500));
                return null;
            }
            return result.Stdout;
        }

        /// <summary>
        /// Duration of a canonical verification clip (16 kHz mono s16le WAV from
        /// the sample extractor), derived from the file size. Null when the file
        /// doesn't look like such a WAV.
        /// </summary>
        private static double? TryGetWavDurationSeconds(string wavPath)
        {
            try
            {
                var length = new FileInfo(wavPath).Length;
                const int headerBytes = 44;
                const double bytesPerSecond = 16000 * 2; // 16 kHz, mono, 16-bit
                if (length <= headerBytes) return null;
                return (length - headerBytes) / bytesPerSecond;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        // Bound the extra decode work: one probe per gap, few gaps expected.
        private const int MaxGapProbes = 3;

        // Smaller holes are normal narration pauses / scene breaks; a credits
        // block needs roughly 10–30s, so 8s catches them without re-probing
        // every dramatic pause.
        private const double MinGapSeconds = 8.0;

        public sealed record TranscriptSegment(double Start, double End, string Text);

        // "[00:00:09.260 --> 00:00:17.160]   by Sarah J. Mass, ..." → segment text.
        private static readonly Regex SegmentTimestampRegex = new(
            @"^\s*\[\d{2}:\d{2}:\d{2}\.\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}\.\d{3}\]\s*",
            RegexOptions.Compiled);

        private static readonly Regex SegmentLineRegex = new(
            @"^\s*\[(?<sh>\d{2}):(?<sm>\d{2}):(?<ss>\d{2})\.(?<sms>\d{3})\s*-->\s*(?<eh>\d{2}):(?<em>\d{2}):(?<es>\d{2})\.(?<ems>\d{3})\]\s*(?<text>.*\S)\s*$",
            RegexOptions.Compiled);

        /// <summary>Parses whisper-cli timestamped lines. Public + pure for unit testing.</summary>
        public static List<TranscriptSegment> ParseSegments(string? stdout)
        {
            var segments = new List<TranscriptSegment>();
            if (string.IsNullOrWhiteSpace(stdout)) return segments;

            foreach (var line in stdout.Split('\n'))
            {
                var match = SegmentLineRegex.Match(line);
                if (!match.Success) continue;
                segments.Add(new TranscriptSegment(
                    ToSeconds(match, "sh", "sm", "ss", "sms"),
                    ToSeconds(match, "eh", "em", "es", "ems"),
                    match.Groups["text"].Value.Trim()));
            }
            return segments;

            static double ToSeconds(Match m, string h, string min, string s, string ms) =>
                int.Parse(m.Groups[h].Value) * 3600
                + int.Parse(m.Groups[min].Value) * 60
                + int.Parse(m.Groups[s].Value)
                + int.Parse(m.Groups[ms].Value) / 1000.0;
        }

        /// <summary>
        /// Silent holes (leading, internal, trailing) of at least
        /// <see cref="MinGapSeconds"/> in a transcribed clip — the spans the
        /// decoder produced nothing for. Public + pure for unit testing.
        /// </summary>
        public static List<(double Start, double End)> FindSilentGaps(
            IReadOnlyList<TranscriptSegment> segments, double clipDurationSeconds)
        {
            var gaps = new List<(double Start, double End)>();
            if (segments.Count == 0) return gaps;

            var cursor = 0.0;
            foreach (var segment in segments.OrderBy(s => s.Start))
            {
                if (segment.Start - cursor >= MinGapSeconds)
                {
                    gaps.Add((cursor, segment.Start));
                }
                cursor = Math.Max(cursor, segment.End);
            }
            if (clipDurationSeconds - cursor >= MinGapSeconds)
            {
                gaps.Add((cursor, clipDurationSeconds));
            }
            return gaps;
        }

        /// <summary>
        /// Collapses whisper-cli's timestamped segment lines into the plain
        /// transcript the matcher consumes. Public + pure for unit testing.
        /// </summary>
        public static string? NormalizeTranscriptOutput(string? stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            var segments = stdout
                .Split('\n')
                .Select(line => SegmentTimestampRegex.Replace(line, string.Empty).Trim())
                .Where(line => line.Length > 0);

            var transcript = string.Join(" ", segments).Trim();
            return transcript.Length == 0 ? null : transcript;
        }

        private static string Truncate(string? text, int max) =>
            string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max]);
    }
}
