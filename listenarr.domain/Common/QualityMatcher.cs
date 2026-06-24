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
    /// Normalised, unit-explicit view of an audio file's quality-relevant facts.
    /// All three call-sites (status evaluator, automatic search, library controller)
    /// hold different carrier types, so they project into this single record before
    /// asking <see cref="QualityMatcher"/> to match against a profile.
    /// </summary>
    public readonly record struct AudioQualityInput
    {
        /// <summary>Raw codec as reported by ffprobe (e.g. "aac", "mp3", "flac", "opus", "vorbis").</summary>
        public string? Codec { get; init; }

        /// <summary>Container (e.g. "M4B", "M4A", "MP4", "OGG").</summary>
        public string? Container { get; init; }

        /// <summary>Format/extension fallback (e.g. "mp3", "flac").</summary>
        public string? Format { get; init; }

        /// <summary>Bitrate in <b>bits per second</b> (the unit used by file/metadata carriers).</summary>
        public int? BitrateBitsPerSecond { get; init; }

        /// <summary>Optional file path, used only as an extension fallback.</summary>
        public string? Path { get; init; }
    }

    /// <summary>Outcome category for <see cref="QualityMatcher.Match"/>.</summary>
    public enum QualityMatchKind
    {
        /// <summary>A profile rung was matched.</summary>
        Matched,

        /// <summary>The file's codec is not present in the profile at all.</summary>
        CodecMismatch,

        /// <summary>The codec matched but the profile has no bitrate rung the file can land on.</summary>
        NoBitrateRung,

        /// <summary>The profile has no qualities to match against.</summary>
        Unknown
    }

    /// <summary>Result of matching a file to a profile.</summary>
    public readonly record struct QualityMatchResult(QualityMatchKind Kind, QualityDefinition? Rung)
    {
        public bool IsMatch => Kind == QualityMatchKind.Matched && Rung is not null;
    }

    /// <summary>
    /// Profile-driven quality matching. The single source of truth for what a profile's
    /// <see cref="QualityDefinition.Priority"/> ordering means (lower number = higher quality)
    /// and how an on-disk file maps onto a profile's quality rungs.
    ///
    /// Replaces the previous string-label classifiers that emitted container labels such as
    /// "M4B" (which never equalled a codec/bitrate rung like "AAC 256kbps", so the cutoff was
    /// never met and the audiobook was re-downloaded forever).
    /// </summary>
    public static class QualityMatcher
    {
        /// <summary>
        /// Match a file to the highest profile rung it meets or exceeds (round-down).
        /// Returns <see cref="QualityMatchKind.CodecMismatch"/> when the file's codec is not in
        /// the profile at all (instead of silently treating it as the lowest possible quality).
        /// </summary>
        public static QualityMatchResult Match(AudioQualityInput file, QualityProfile? profile)
        {
            if (profile?.Qualities == null || profile.Qualities.Count == 0)
            {
                return new QualityMatchResult(QualityMatchKind.Unknown, null);
            }

            var fileGroups = MapCodec(file);
            var fileIsLossless = IsLosslessFile(file);
            var fileKbps = NormalizeKbps(file.BitrateBitsPerSecond);

            var allowed = AllowedQualities(profile).ToList();
            if (allowed.Count == 0)
            {
                return new QualityMatchResult(QualityMatchKind.Unknown, null);
            }

            var effective = MatchableQualities(profile)
                .Select(EffectiveRung)
                .ToList();

            if (effective.Count == 0)
            {
                return new QualityMatchResult(QualityMatchKind.Unknown, null);
            }

            // Codec-specific rungs take precedence; wildcard (codec-less) rungs are the fallback
            // and exist mainly for legacy/bare profiles such as "320kbps" / "lossless".
            var codecCandidates = effective
                .Where(r => r.Codec != null
                            && r.IsLossless == fileIsLossless
                            && fileGroups.Contains(r.Codec))
                .ToList();

            var pool = codecCandidates.Count > 0
                ? codecCandidates
                : effective.Where(r => r.Codec == null && r.IsLossless == fileIsLossless).ToList();

            if (pool.Count == 0)
            {
                return new QualityMatchResult(QualityMatchKind.CodecMismatch, null);
            }

            // Lossless files ignore bitrate: take the best (lowest-priority) lossless rung.
            if (fileIsLossless)
            {
                return ToAllowedMatch(Best(pool).Source);
            }

            var withBitrate = pool.Where(r => r.BitrateKbps is not null).ToList();
            var vbr = pool.Where(r => r.BitrateKbps is null).ToList();

            if (fileKbps is int kbps)
            {
                var eligible = withBitrate.Where(r => r.BitrateKbps <= kbps).ToList();
                if (eligible.Count > 0)
                {
                    return ToAllowedMatch(Best(eligible).Source);
                }

                // File is below the lowest configured rung: fall to the worst rung (never over-claim).
                if (withBitrate.Count > 0)
                {
                    return ToAllowedMatch(Worst(withBitrate).Source);
                }

                if (vbr.Count > 0)
                {
                    return ToAllowedMatch(Best(vbr).Source);
                }

                return new QualityMatchResult(QualityMatchKind.NoBitrateRung, null);
            }

            // Unknown bitrate: prefer a VBR rung, else conservatively the worst bitrate rung.
            if (vbr.Count > 0)
            {
                return ToAllowedMatch(Best(vbr).Source);
            }

            if (withBitrate.Count > 0)
            {
                return ToAllowedMatch(Worst(withBitrate).Source);
            }

            return new QualityMatchResult(QualityMatchKind.NoBitrateRung, null);

            static QualityMatchResult ToAllowedMatch(QualityDefinition rung)
                => rung.Allowed
                    ? new QualityMatchResult(QualityMatchKind.Matched, rung)
                    : new QualityMatchResult(QualityMatchKind.NoBitrateRung, null);
        }

        /// <summary>The profile rung label a file maps to, or null if it does not match.</summary>
        public static string? MatchLabel(AudioQualityInput file, QualityProfile? profile)
            => Match(file, profile).Rung?.Quality;

        /// <summary>
        /// Whether a file meets or exceeds the profile cutoff. A blank cutoff is always met;
        /// a missing/codec-mismatched file is not.
        /// </summary>
        public static bool MeetsCutoff(AudioQualityInput file, QualityProfile? profile)
        {
            var cutoff = ResolveCutoff(profile, out var cutoffBlank);
            if (cutoffBlank)
            {
                return true;
            }

            if (cutoff == null)
            {
                return false;
            }

            var match = Match(file, profile);
            return match.IsMatch && match.Rung!.Priority <= cutoff.Priority;
        }

        /// <summary>
        /// Whether a known quality <paramref name="qualityLabel"/> (e.g. a value stored in download
        /// metadata) meets or exceeds the profile cutoff.
        /// </summary>
        public static bool LabelMeetsCutoff(string? qualityLabel, QualityProfile? profile)
        {
            var cutoff = ResolveCutoff(profile, out var cutoffBlank);
            if (cutoffBlank)
            {
                return true;
            }

            if (cutoff == null || string.IsNullOrWhiteSpace(qualityLabel))
            {
                return false;
            }

            var rung = FindAllowedRung(profile!, qualityLabel);
            return rung != null && rung.Priority <= cutoff.Priority;
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> is strictly higher quality than <paramref name="existing"/>
        /// (lower priority number). An unknown candidate is never better; an unknown existing is always beaten.
        /// </summary>
        public static bool IsLabelBetter(string? candidate, string? existing, QualityProfile? profile)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            if (profile == null)
            {
                return false;
            }

            var cand = FindAllowedRung(profile, candidate);

            if (cand == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(existing))
            {
                return true;
            }

            var exist = FindAllowedRung(profile, existing);

            if (exist == null)
            {
                return true;
            }

            return cand.Priority < exist.Priority;
        }

        // ---- internals --------------------------------------------------------------------

        private readonly record struct EffectiveRungInfo(QualityDefinition Source, string? Codec, int? BitrateKbps, bool IsLossless)
        {
            public int Priority => Source.Priority;
        }

        private static EffectiveRungInfo Best(IEnumerable<EffectiveRungInfo> rungs)
            => rungs.OrderBy(r => r.Priority).First();

        private static EffectiveRungInfo Worst(IEnumerable<EffectiveRungInfo> rungs)
            => rungs.OrderByDescending(r => r.Priority).First();

        private static QualityDefinition? ResolveCutoff(QualityProfile? profile, out bool cutoffBlank)
        {
            cutoffBlank = false;
            if (profile?.Qualities == null || profile.Qualities.Count == 0
                || string.IsNullOrWhiteSpace(profile.CutoffQuality))
            {
                cutoffBlank = true;
                return null;
            }

            return FindAllowedRung(profile, profile.CutoffQuality);
        }

        private static IEnumerable<QualityDefinition> AllowedQualities(QualityProfile profile)
            => profile.Qualities
                .Where(q => q.Allowed && !string.IsNullOrWhiteSpace(q.Quality));

        private static IEnumerable<QualityDefinition> MatchableQualities(QualityProfile profile)
            => profile.Qualities
                .Where(q => !string.IsNullOrWhiteSpace(q.Quality));

        private static QualityDefinition? FindAllowedRung(QualityProfile profile, string label)
            => AllowedQualities(profile)
                .FirstOrDefault(q => string.Equals(q.Quality, label, StringComparison.OrdinalIgnoreCase));

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
            if (Contains(lower, "aac") || Contains(lower, "m4b") || Contains(lower, "m4a")) return ("AAC", bitrate, false);
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
            // AAC commonly lives in M4B/M4A/MP4 containers; cover the legacy "M4B" codec group too.
            if (Any("aac") || Any("m4b") || Any("m4a") || Any("mp4"))
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
            if (Contains(lower, "aac") || Contains(lower, "m4b") || Contains(lower, "m4a")) return "AAC";
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
