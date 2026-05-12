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

        // Bitrate rungs seeded by QualityProfileService.EnsureProfileHasRequiredQualitiesAsync.
        // Ordered high → low so BucketBitrate returns the largest rung the file meets.
        private static readonly int[] BitrateBuckets = { 320, 256, 192, 128, 64 };

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
            // Only trust the audiobookQuality hint if it already matches a production QP key
            // shape (e.g. "MP3 320kbps", "AAC 64kbps", "FLAC"). Legacy values like "M4B" must
            // fall through so we re-derive from the file's codec/container/bitrate fields and
            // produce a label that can actually round-trip with QualityProfile.Qualities keys.
            var hint = Normalize(audiobookQuality);
            if (IsCodecPrefixedLabel(hint))
            {
                return hint;
            }

            var codec = DetectCodec(file);

            if (IsLosslessCodec(codec))
            {
                return "lossless";
            }

            if (!string.IsNullOrEmpty(codec) && file?.Bitrate is int bitrate)
            {
                var bitrateKbps = bitrate >= 1000 ? bitrate / 1000d : bitrate;
                var bucket = BucketBitrate(bitrateKbps);
                if (bucket > 0)
                {
                    return $"{codec} {bucket}kbps";
                }
            }

            return Normalize(file?.Format);
        }

        private static string DetectCodec(AudiobookFileStatusInfo? file)
        {
            var codec = Normalize(file?.Codec);
            var container = Normalize(file?.Container);
            var format = Normalize(file?.Format);

            if (codec.Contains("mp3", StringComparison.Ordinal)
                || container.Contains("mp3", StringComparison.Ordinal)
                || format == "mp3")
            {
                return "mp3";
            }

            if (codec.Contains("flac", StringComparison.Ordinal)
                || container.Contains("flac", StringComparison.Ordinal)
                || format == "flac")
            {
                return "flac";
            }

            if (codec.Contains("alac", StringComparison.Ordinal)
                || container.Contains("alac", StringComparison.Ordinal))
            {
                return "alac";
            }

            if (codec.Contains("opus", StringComparison.Ordinal)
                || container.Contains("opus", StringComparison.Ordinal)
                || format == "opus")
            {
                return "opus";
            }

            if (codec.Contains("vorbis", StringComparison.Ordinal)
                || container.Contains("ogg", StringComparison.Ordinal)
                || format == "ogg")
            {
                return "vorbis";
            }

            if (codec.Contains("aac", StringComparison.Ordinal)
                || codec.Contains("mp4a", StringComparison.Ordinal))
            {
                return "aac";
            }

            if (codec.Contains("aiff", StringComparison.Ordinal)
                || container.Contains("aiff", StringComparison.Ordinal))
            {
                return "aiff";
            }

            if (codec.Contains("ape", StringComparison.Ordinal)
                || container.Contains("ape", StringComparison.Ordinal))
            {
                return "ape";
            }

            if (codec.Contains("dsd", StringComparison.Ordinal)
                || container.Contains("dsd", StringComparison.Ordinal))
            {
                return "dsd";
            }

            if (codec.Contains("wavpack", StringComparison.Ordinal)
                || container == "wv")
            {
                return "wavpack";
            }

            if (codec.Contains("wav", StringComparison.Ordinal)
                || container == "wav"
                || format == "wav")
            {
                return "wav";
            }

            // M4B/M4A/MP4 containers carry AAC for virtually all audiobooks.
            if (container is "m4b" or "m4a" or "mp4"
                || format is "m4b" or "m4a" or "mp4")
            {
                return "aac";
            }

            return string.Empty;
        }

        private static bool IsLosslessCodec(string codec)
        {
            return codec is "flac" or "alac" or "wav" or "aiff" or "ape" or "dsd" or "wavpack";
        }

        private static int BucketBitrate(double bitrateKbps)
        {
            foreach (var bucket in BitrateBuckets)
            {
                if (bitrateKbps >= bucket)
                {
                    return bucket;
                }
            }
            // Below the lowest seeded rung — map to it so we don't trigger
            // a perpetual re-grab loop for unusually low-bitrate sources.
            return BitrateBuckets[^1];
        }

        private static bool IsCodecPrefixedLabel(string normalizedLabel)
        {
            if (string.IsNullOrEmpty(normalizedLabel))
            {
                return false;
            }

            return normalizedLabel.StartsWith("mp3 ", StringComparison.Ordinal)
                || normalizedLabel.StartsWith("aac ", StringComparison.Ordinal)
                || normalizedLabel == "mp3 vbr"
                || normalizedLabel == "flac";
        }

        private static string Normalize(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
