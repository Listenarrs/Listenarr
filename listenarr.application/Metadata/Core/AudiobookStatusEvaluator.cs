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

namespace Listenarr.Application.Metadata.Core
{
    public static class AudiobookStatusEvaluator
    {
        public const string Downloading = "downloading";
        public const string NoFile = "no-file";
        public const string QualityMismatch = "quality-mismatch";
        public const string QualityMatch = "quality-match";

        public static string ComputeStatus(
            bool isDownloading,
            bool hasAnyFile,
            string? audiobookQuality,
            QualityProfile? qualityProfile,
            IReadOnlyList<AudiobookFormatSummary>? files)
        {
            if (isDownloading)
            {
                return Downloading;
            }

            if (!hasAnyFile)
            {
                return NoFile;
            }

            if (qualityProfile == null)
            {
                return QualityMatch;
            }

            var preferredFormats = (qualityProfile.PreferredFormats ?? new List<string>())
                .Select(Normalize)
                .Where(v => v.Length > 0)
                .ToList();

            var candidateFiles = (files ?? Array.Empty<AudiobookFormatSummary>())
                .Where(f =>
                {
                    var fileFormat = Normalize(f.Format);
                    if (fileFormat.Length == 0)
                    {
                        fileFormat = Normalize(f.Container);
                    }
                    if (fileFormat.Length == 0)
                    {
                        // Path-only file (no probe metadata): fall back to the path extension so a
                        // metadata-less book.flac still satisfies a PreferredFormats = ["flac"] filter
                        // instead of being dropped before QualityMatcher can use the extension.
                        fileFormat = ExtensionFromPath(f.Path);
                    }

                    if (preferredFormats.Count == 0)
                    {
                        return true;
                    }

                    return preferredFormats.Contains(fileFormat)
                        || preferredFormats.Any(pf => fileFormat.Contains(pf, StringComparison.Ordinal));
                })
                .ToList();

            if (candidateFiles.Count == 0)
            {
                if (files == null || files.Count == 0)
                {
                    return QualityMatch;
                }

                return QualityMismatch;
            }

            if (string.IsNullOrWhiteSpace(qualityProfile.CutoffQuality)
                || qualityProfile.Qualities == null
                || qualityProfile.Qualities.Count == 0)
            {
                return QualityMatch;
            }

            // A pinned audiobook-level quality short-circuits per-file derivation.
            if (!string.IsNullOrWhiteSpace(audiobookQuality))
            {
                return QualityMatcher.LabelMeetsCutoff(audiobookQuality, qualityProfile)
                    ? QualityMatch
                    : QualityMismatch;
            }

            foreach (var file in candidateFiles)
            {
                var input = new AudioQualityInput
                {
                    Codec = file.Codec,
                    Container = file.Container,
                    Format = file.Format,
                    BitrateBitsPerSecond = file.Bitrate,
                    // Path is the only quality signal when metadata processing is disabled,
                    // ffprobe is unavailable, or extraction failed. Mirrors AudiobookQualityCutoffEvaluator
                    // so automatic-search and library status agree for path-only files.
                    Path = file.Path
                };

                if (QualityMatcher.MeetsCutoff(input, qualityProfile))
                {
                    return QualityMatch;
                }
            }

            return QualityMismatch;
        }

        private static string Normalize(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string ExtensionFromPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return Normalize(System.IO.Path.GetExtension(path).TrimStart('.'));
        }
    }
}
