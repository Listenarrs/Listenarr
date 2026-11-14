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
    public static partial class QualityMatcher
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
                var eligible = withBitrate.Where(r => MeetsRung(kbps, r.BitrateKbps!.Value)).ToList();
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

        /// <summary>
        /// The codec group a free-text quality label belongs to ("FLAC", "AAC", "MP3", "OPUS", ...),
        /// or null when the label names no codec at all. A bare bitrate such as "320kbps" comes
        /// back null because it says nothing about the codec, and so does any label this method
        /// does not recognise.
        ///
        /// Recognition is <see cref="ParseQualityLabel"/>'s, and it is kept in step with
        /// <see cref="MapCodec"/> on containers: "M4B", "M4A", "MP4", "AAX" and "AAXC" all resolve
        /// to AAC in both. They used to disagree, so an Audible AAX rip came back null here and a
        /// caller asking whether the profile had an opinion was told it had none.
        ///
        /// Null still means what it has always meant: this method cannot name a codec for the
        /// label. It is not a verdict, and callers should not read it as permission. What one
        /// caller does with it: <see cref="QualityGate"/> treats null as a refusal. Another caller
        /// is free to differ, so do not rely on that here.
        /// </summary>
        public static string? CodecGroupOfLabel(string? qualityLabel)
            => string.IsNullOrWhiteSpace(qualityLabel) ? null : ParseQualityLabel(qualityLabel).Codec;

        /// <summary>
        /// The codec group a profile rung belongs to, preferring its structured
        /// <see cref="QualityDefinition.Codec"/> and parsing its label otherwise, since seed and
        /// legacy rungs carry only Quality and Priority.
        /// </summary>
        public static string? CodecGroupOfRung(QualityDefinition? rung)
        {
            if (rung is null)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(rung.Codec)
                ? CodecGroupOfLabel(rung.Quality)
                : CanonicalCodec(rung.Codec!);
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

        /// <summary>
        /// The rung the profile stops upgrading at, or null with <paramref name="cutoffBlank"/>
        /// set when the profile is not upgrading at all.
        /// </summary>
        /// <remarks>
        /// A profile with <see cref="QualityProfile.UpgradeAllowed"/> false counts as blank here
        /// even when it carries a real cutoff, because it is not going to upgrade past anything.
        /// Before that flag existed the only way to record "upgrades off" was to blank the cutoff,
        /// so the two arms of this test used to be the same arm, and callers that already treat a
        /// blank cutoff as satisfied keep the answer they had.
        ///
        /// Readarr and Sonarr reach the same outcome by a different route, and the difference is
        /// worth naming because it is where a reviewer will look. They do not switch the cutoff
        /// off; they lower it, with
        /// <c>var cutoff = profile.UpgradeAllowed ? profile.Cutoff : profile.FirstAllowedQuality().Id;</c>
        /// (src/NzbDrone.Core/DecisionEngine/Specifications/UpgradableSpecification.cs:99 in
        /// Readarr; Sonarr's :126 is the same line, spelling its own helper FirststAllowedQuality).
        /// The flag is then checked again on its own, and refuses the upgrade outright: Sonarr
        /// returns UpgradeableRejectReason.UpgradesNotAllowed at :64 and Readarr's
        /// CheckUpgradeAllowed returns false at :171-174. So the file is not replaced there
        /// either.
        ///
        /// Listenarr has one question instead of two, and answers it here. That keeps the flag and
        /// the blank cutoff it replaces on the same code path, which is what lets every profile
        /// that has been recording upgrades-off as a blank cutoff keep the answer it had. The cost
        /// is that "meets cutoff" reports true for a file the family would call below cutoff while
        /// still declining to replace it, so the two agree on what happens and disagree on what to
        /// call it.
        /// </remarks>
        private static QualityDefinition? ResolveCutoff(QualityProfile? profile, out bool cutoffBlank)
        {
            cutoffBlank = false;
            if (profile?.Qualities == null || profile.Qualities.Count == 0
                || !profile.UpgradeAllowed
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
    }
}
