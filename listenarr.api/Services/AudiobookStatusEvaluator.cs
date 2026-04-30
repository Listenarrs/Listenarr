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
namespace Listenarr.Api.Services
{
    public sealed class AudiobookFileStatusInfo
    {
        public int AudiobookId { get; set; }
        public string? Path { get; set; }
        public string? Format { get; set; }
        public string? Container { get; set; }
        public string? Codec { get; set; }
        public int? Bitrate { get; set; }
    }

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
            IReadOnlyList<AudiobookFileStatusInfo>? files)
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

            var candidateFiles = (files ?? Array.Empty<AudiobookFileStatusInfo>())
                .Where(f =>
                {
                    var fileFormat = Normalize(f.Format);
                    if (fileFormat.Length == 0)
                    {
                        fileFormat = Normalize(f.Container);
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

            var qualityPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var quality in qualityProfile.Qualities)
            {
                if (quality == null || string.IsNullOrWhiteSpace(quality.Quality))
                {
                    continue;
                }

                qualityPriority[Normalize(quality.Quality)] = quality.Priority;
            }

            var cutoff = Normalize(qualityProfile.CutoffQuality);
            var cutoffPriority = qualityPriority.TryGetValue(cutoff, out var foundCutoffPriority)
                ? foundCutoffPriority
                : int.MaxValue;

            foreach (var derivedQuality in candidateFiles.Select(file => DeriveQualityLabel(file, audiobookQuality)))
            {
                if (derivedQuality.Length == 0)
                {
                    continue;
                }

                var priority = qualityPriority.TryGetValue(derivedQuality, out var foundPriority)
                    ? foundPriority
                    : int.MaxValue;

                if (priority <= cutoffPriority)
                {
                    return QualityMatch;
                }
            }

            return QualityMismatch;
        }

        private static string DeriveQualityLabel(AudiobookFileStatusInfo? file, string? audiobookQuality)
        {
            var normalizedAudiobookQuality = Normalize(audiobookQuality);
            if (normalizedAudiobookQuality.Length > 0)
            {
                return normalizedAudiobookQuality;
            }

            if (file?.Bitrate is int bitrate)
            {
                var bitrateKbps = bitrate >= 1000 ? bitrate / 1000d : bitrate;

                if (bitrateKbps >= 320)
                {
                    return "320kbps";
                }

                if (bitrateKbps >= 256)
                {
                    return "256kbps";
                }

                if (bitrateKbps >= 192)
                {
                    return "192kbps";
                }

                return $"{Math.Round(bitrateKbps)}kbps";
            }

            var container = Normalize(file?.Container);
            var codec = Normalize(file?.Codec);
            if (container.Contains("flac", StringComparison.Ordinal)
                || codec.Contains("flac", StringComparison.Ordinal)
                || container.Contains("alac", StringComparison.Ordinal)
                || codec.Contains("alac", StringComparison.Ordinal)
                || container.Contains("aiff", StringComparison.Ordinal)
                || codec.Contains("aiff", StringComparison.Ordinal)
                || container.Contains("ape", StringComparison.Ordinal)
                || codec.Contains("ape", StringComparison.Ordinal)
                || container.Contains("dsd", StringComparison.Ordinal)
                || codec.Contains("dsd", StringComparison.Ordinal)
                || container.Contains("wv", StringComparison.Ordinal)
                || codec.Contains("wv", StringComparison.Ordinal)
                || container.Contains("wav", StringComparison.Ordinal)
                || codec.Contains("wav", StringComparison.Ordinal))
            {
                return "lossless";
            }

            return Normalize(file?.Format);
        }

        private static string Normalize(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
