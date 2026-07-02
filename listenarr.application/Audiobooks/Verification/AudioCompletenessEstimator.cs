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
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Estimates how much of a book's catalog runtime is actually present on
    /// disk (ADR-0001 completeness check). Live motivating case: a record whose
    /// import delivered parts 10 and 20 of a ~20-part set — 42 minutes of a
    /// 17-hour book — was flagged Mismatch 0.95 because the sampled "opening"
    /// was a mid-book cold open; the right diagnosis is "partial content of the
    /// right book", which only the runtime comparison can see.
    /// </summary>
    public static class AudioCompletenessEstimator
    {
        /// <summary>
        /// Null when the catalog runtime is unknown or no tracked file carries a
        /// usable duration. Files whose duration probe failed (a real ffprobe
        /// outcome on damaged headers) are extrapolated from their byte size at
        /// the average bitrate of the files that DO have durations.
        /// </summary>
        public static VerificationCompleteness? Estimate(Audiobook audiobook)
        {
            var expectedMinutes = audiobook.Runtime ?? 0;
            if (expectedMinutes <= 0) return null;

            var audioFiles = (audiobook.Files ?? new List<AudiobookFile>())
                .Where(f => !string.IsNullOrWhiteSpace(f.Path) && FileUtils.IsAudioFile(f.Path!))
                .ToList();
            if (audioFiles.Count == 0) return null;

            var knownSeconds = 0.0;
            var pairedSeconds = 0.0;
            var pairedBytes = 0L;
            var unknownBytes = 0L;
            foreach (var file in audioFiles)
            {
                if (file.DurationSeconds is > 0)
                {
                    knownSeconds += file.DurationSeconds.Value;
                    // The bitrate anchor must come only from files where duration
                    // and size are BOTH known. A file with a duration but a null
                    // size adds seconds without bytes, shrinking the average
                    // bytes-per-second and inflating every extrapolated file —
                    // a live record showed "363h of 12h expected (3027%)".
                    if (file.Size is > 0)
                    {
                        pairedSeconds += file.DurationSeconds.Value;
                        pairedBytes += file.Size.Value;
                    }
                }
                else if (file.Size is > 0)
                {
                    unknownBytes += file.Size.Value;
                }
            }
            if (knownSeconds <= 0) return null;

            var estimatedUnknownSeconds = pairedSeconds > 0 && pairedBytes > 0
                ? unknownBytes / (pairedBytes / (double)pairedSeconds)
                : 0.0;

            var actualMinutes = (knownSeconds + estimatedUnknownSeconds) / 60.0;
            return new VerificationCompleteness(
                ExpectedMinutes: expectedMinutes,
                ActualMinutes: Math.Round(actualMinutes, 1),
                Coverage: Math.Round(actualMinutes / expectedMinutes, 3));
        }
    }
}
