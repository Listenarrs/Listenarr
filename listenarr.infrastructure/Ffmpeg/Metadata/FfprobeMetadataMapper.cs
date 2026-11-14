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
using System.Globalization;
using System.Text.Json;

namespace Listenarr.Infrastructure.Ffmpeg.Metadata
{
    internal static class FfprobeMetadataMapper
    {
        public static AudioMetadata Map(JsonElement ffprobeData, string filePath)
        {
            var metadata = new AudioMetadata();

            if (ffprobeData.TryGetProperty("format", out var fmt))
            {
                ApplyFormat(metadata, fmt, filePath);
            }

            if (ffprobeData.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                ApplyAudioStream(metadata, streams);
            }

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrEmpty(metadata.Title)) metadata.Title = fileName;
            if (string.IsNullOrEmpty(metadata.Format)) metadata.Format = Path.GetExtension(filePath).TrimStart('.').ToUpper();
            if (string.IsNullOrEmpty(metadata.Container)) metadata.Container = Path.GetExtension(filePath).TrimStart('.').ToUpper();

            return metadata;
        }

        private static void ApplyFormat(AudioMetadata metadata, JsonElement fmt, string filePath)
        {
            if (fmt.TryGetProperty("duration", out var durEl)
                && durEl.ValueKind == JsonValueKind.String
                && double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
            {
                metadata.Duration = TimeSpan.FromSeconds(dur);
            }

            if (fmt.TryGetProperty("format_name", out var fmtName) && fmtName.ValueKind == JsonValueKind.String)
            {
                ApplyFormatName(metadata, fmtName.GetString() ?? string.Empty, filePath);
            }

            if (fmt.TryGetProperty("bit_rate", out var br) && br.ValueKind == JsonValueKind.String && int.TryParse(br.GetString(), out var bitRate))
            {
                metadata.BitRate = bitRate;
            }

            if (fmt.TryGetProperty("tags", out var formatTags) && formatTags.ValueKind == JsonValueKind.Object)
            {
                FfprobeTagMetadataMapper.Apply(metadata, formatTags);
            }
        }

        private static void ApplyFormatName(AudioMetadata metadata, string rawFormat, string filePath)
        {
            var primary = rawFormat.Split(',')[0];
            var ext = Path.GetExtension(filePath)?.TrimStart('.')?.ToLowerInvariant();

            if (!string.IsNullOrEmpty(ext))
            {
                if (ext == "m4b")
                {
                    metadata.Format = ext.ToUpperInvariant();
                    metadata.Container = ext.ToUpperInvariant();
                }
                else
                {
                    metadata.Format = primary.ToUpperInvariant();
                    metadata.Container = primary.ToUpperInvariant();
                }
            }
            else
            {
                metadata.Format = primary.ToUpperInvariant();
                metadata.Container = primary.ToUpperInvariant();
            }
        }

        private static void ApplyAudioStream(AudioMetadata metadata, JsonElement streams)
        {
            foreach (var s in streams
                .EnumerateArray()
                .Where(s => s.TryGetProperty("codec_type", out var codecType) && codecType.GetString() == "audio"))
            {
                if (s.TryGetProperty("sample_rate", out var sr) && sr.ValueKind == JsonValueKind.String && int.TryParse(sr.GetString(), out var sampleRate))
                {
                    metadata.SampleRate = sampleRate;
                }
                if (s.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Number)
                {
                    metadata.Channels = ch.GetInt32();
                }
                if (s.TryGetProperty("bit_rate", out var sbr) && sbr.ValueKind == JsonValueKind.String && int.TryParse(sbr.GetString(), out var sbit))
                {
                    metadata.BitRate = metadata.BitRate == 0 ? sbit : metadata.BitRate;
                }
                if (s.TryGetProperty("codec_name", out var codecName) && codecName.ValueKind == JsonValueKind.String)
                {
                    metadata.Codec = codecName.GetString();
                }
                if (s.TryGetProperty("tags", out var streamTags) && streamTags.ValueKind == JsonValueKind.Object)
                {
                    FfprobeTagMetadataMapper.Apply(metadata, streamTags);
                }
                break;
            }
        }
    }
}
