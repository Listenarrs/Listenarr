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
using System.Text.RegularExpressions;

namespace Listenarr.Domain.Common
{
    /// <summary>
    /// Codec, container and bitrate helpers for <see cref="QualityMatcher"/>: how a rung label or a
    /// file resolves to a codec group and a bitrate, and how a bitrate meets a rung.
    /// </summary>
    public static partial class QualityMatcher
    {
        /// <summary>
        /// Resolve a rung's effective (codec group, bitrate-kbps, lossless) using the structured
        /// fields when present and parsing the <see cref="QualityDefinition.Quality"/> label otherwise
        /// (seed/legacy rungs only set Quality + Priority).
        /// </summary>
        private static EffectiveRungInfo EffectiveRung(QualityDefinition rung)
        {
            if (!string.IsNullOrWhiteSpace(rung.Codec))
            {
                return new EffectiveRungInfo(rung, CanonicalCodec(rung.Codec), rung.Bitrate, rung.IsLossless);
            }

            var (codec, bitrate, lossless) = ParseQualityLabel(rung.Quality);
            return new EffectiveRungInfo(rung, codec, rung.Bitrate ?? bitrate, rung.IsLossless || lossless);
        }

        private static (string? Codec, int? BitrateKbps, bool IsLossless) ParseQualityLabel(string quality)
        {
            var lower = (quality ?? string.Empty).Trim().ToLowerInvariant();

            int? bitrate = null;
            var match = Regex.Match(lower, @"\d{2,}");
            if (match.Success && int.TryParse(match.Value, out var kb))
            {
                bitrate = kb;
            }

            if (Contains(lower, "flac")) return ("FLAC", bitrate, true);
            if (Contains(lower, "alac")) return ("ALAC", bitrate, true);
            // Every MPEG-4 container carries AAC, so they all resolve to the AAC group: "M4B" and
            // "M4A" as before, "MP4" because MapCodec has always accepted it here and the two
            // diverging left a label this method could not place, and "AAX"/"AAXC" because those
            // are Audible's MPEG-4 containers and the scorer ranks AAX second only to FLAC.
            // "aaxc" is covered by the "aax" test.
            if (Contains(lower, "aac") || Contains(lower, "m4b") || Contains(lower, "m4a")
                || Contains(lower, "mp4") || Contains(lower, "aax")) return ("AAC", bitrate, false);
            if (Contains(lower, "mp3")) return ("MP3", bitrate, false);
            if (Contains(lower, "opus")) return ("OPUS", bitrate, false);
            if (Contains(lower, "vorbis") || Contains(lower, "ogg")) return ("OGG Vorbis", bitrate, false);
            if (Contains(lower, "aiff")) return ("AIFF", bitrate, true);
            if (Contains(lower, "ape")) return ("APE", bitrate, true);
            if (Contains(lower, "dsd")) return ("DSD", bitrate, true);
            if (Contains(lower, "wav") || Contains(lower, "wv")) return ("WavPack", bitrate, true);
            if (Contains(lower, "lossless")) return (null, bitrate, true);

            // Bare bitrate (e.g. "320kbps") acts as a codec-agnostic wildcard rung.
            return (null, bitrate, false);
        }

        /// <summary>Map a file's codec/container/format/extension onto the set of profile codec groups it satisfies.</summary>
        private static HashSet<string> MapCodec(AudioQualityInput file)
        {
            var tokens = new List<string>();
            AddToken(tokens, file.Codec);
            AddToken(tokens, file.Container);
            AddToken(tokens, file.Format);
            if (!string.IsNullOrWhiteSpace(file.Path))
            {
                AddToken(tokens, System.IO.Path.GetExtension(file.Path)?.TrimStart('.'));
            }

            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool Any(string needle) => tokens.Any(t => Contains(t, needle));

            if (Any("flac")) groups.Add("FLAC");
            if (Any("alac")) groups.Add("ALAC");
            if (Any("aiff")) groups.Add("AIFF");
            if (Any("ape")) groups.Add("APE");
            if (Any("dsd")) groups.Add("DSD");
            if (Any("wav") || Any("wv")) groups.Add("WavPack");
            if (Any("mp3")) groups.Add("MP3");
            if (Any("opus")) groups.Add("OPUS");
            if (Any("vorbis") || Any("ogg")) groups.Add("OGG Vorbis");
            // AAC commonly lives in M4B/M4A/MP4/AAX containers; cover the legacy "M4B" codec group
            // too. This family is deliberately kept in step across ParseQualityLabel, MapCodec and
            // CanonicalCodec, because when they disagree a label the gate can place maps to a codec
            // group the matcher cannot, or the reverse. The three do NOT agree outside it:
            // CanonicalCodec handles no aiff, ape, dsd, wav/wv or lossless and returns the raw
            // string for them, which mostly hides behind the gate's OrdinalIgnoreCase comparison.
            // That predates this change; do not read the MPEG-4 agreement as a general property.
            if (Any("aac") || Any("m4b") || Any("m4a") || Any("mp4") || Any("aax"))
            {
                groups.Add("AAC");
                groups.Add("M4B");
            }

            return groups;
        }

        /// <summary>Codec groups that represent lossless audio (see <see cref="MapCodec"/>).</summary>
        private static readonly HashSet<string> LosslessGroups =
            new(StringComparer.OrdinalIgnoreCase) { "FLAC", "ALAC", "AIFF", "APE", "DSD", "WavPack" };

        private static bool IsLosslessFile(AudioQualityInput file)
        {
            // Derive lossless-ness from the same mapped codec groups used for matching, so the
            // path extension fallback (e.g. "book.flac" with no codec/container/format metadata)
            // is honoured consistently — otherwise such a file maps to the FLAC group yet is
            // treated as lossy and filtered off the FLAC rung.
            return MapCodec(file).Overlaps(LosslessGroups);
        }

        /// <summary>
        /// How far below a rung a file may report and still count as that rung, as a fraction.
        ///
        /// A file encoded at a nominal bitrate almost never reports exactly that figure. A "128kbps"
        /// AAC file commonly reports something like 127241 bps, which rounds to 127 kbps, and a
        /// strict `rung &lt;= file` comparison then excludes the 128 rung and drops the file a whole
        /// tier. Applied to a real library that misclassifies most lossy files, because the failure
        /// is systematic rather than occasional.
        ///
        /// Five percent is far smaller than the gap between adjacent rungs in any ordinary profile.
        /// Across 64, 96, 128, 192, 256 and 320 the narrowest gap is 256 to 320, a fifth of the
        /// higher rung and four times this tolerance, so a constant-bitrate file cannot be promoted
        /// across a real tier boundary. It only absorbs encoder variance.
        /// </summary>
        private const double RungBitrateTolerance = 0.05;

        /// <summary>
        /// Whether a file's bitrate reaches a rung, allowing for the difference between a nominal
        /// bitrate and what an encoder actually reports.
        /// </summary>
        private static bool MeetsRung(int fileKbps, int rungKbps)
        {
            if (fileKbps >= rungKbps)
            {
                return true;
            }

            // At least one whole kbps of slack, so the smallest rungs are not left with a tolerance
            // that rounds away to nothing.
            var slack = Math.Max(1d, rungKbps * RungBitrateTolerance);
            return rungKbps - fileKbps <= slack;
        }

        /// <summary>Convert a bitrate to kbps, guarding values already expressed in kbps.</summary>
        private static int? NormalizeKbps(int? bitsPerSecond)
        {
            if (bitsPerSecond is not int bps || bps <= 0)
            {
                return null;
            }

            return bps >= 1000 ? (int)Math.Round(bps / 1000d) : bps;
        }

        private static string CanonicalCodec(string codec)
        {
            var lower = codec.Trim().ToLowerInvariant();
            if (Contains(lower, "flac")) return "FLAC";
            if (Contains(lower, "alac")) return "ALAC";
            if (Contains(lower, "aac") || Contains(lower, "m4b") || Contains(lower, "m4a")
                || Contains(lower, "mp4") || Contains(lower, "aax")) return "AAC";
            if (Contains(lower, "mp3")) return "MP3";
            if (Contains(lower, "opus")) return "OPUS";
            if (Contains(lower, "vorbis") || Contains(lower, "ogg")) return "OGG Vorbis";
            return codec.Trim();
        }

        private static void AddToken(List<string> tokens, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                tokens.Add(value.Trim().ToLowerInvariant());
            }
        }

        private static bool Contains(string haystack, string needle)
            => haystack.Contains(needle, StringComparison.Ordinal);
    }
}
