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
    /// <summary>What a quality profile has to say about one quality label.</summary>
    public enum QualityGateVerdict
    {
        /// <summary>The profile carries a rung covering this quality and permits it.</summary>
        Allowed,

        /// <summary>
        /// The profile refuses this quality. Either it carries rungs covering the quality and
        /// none of them is allowed, or no rung covers the quality and no PreferredFormats token
        /// names it, which includes a label the gate cannot place at all.
        /// </summary>
        Refused,

        /// <summary>
        /// The profile describes nothing that covers this quality: there is no label to judge,
        /// or no ladder to judge it against, or the ladder carries no rung for the label's codec
        /// and one of the profile's PreferredFormats names it.
        /// </summary>
        NoOpinion
    }

    /// <summary>
    /// Reads a quality profile's <see cref="QualityDefinition.Allowed"/> flags as a gate.
    ///
    /// A profile expresses two separate things about a release. Its quality rungs say what is
    /// permitted; its <see cref="QualityProfile.PreferredFormats"/> say what is preferred among
    /// the permitted. Wherever a rung covers the label, only the rungs decide, and a preferred
    /// format cannot override an unticked rung. The scorer applies preferences as a score
    /// adjustment. The one place PreferredFormats reaches the gate is a label no rung covers,
    /// where it decides the way the allow-list this replaced did (see <see cref="Evaluate"/>).
    /// Sonarr and Readarr keep the two apart: their QualityAllowedByProfileSpecification is a
    /// boolean veto that no custom-format score can override, and their ladders list every
    /// quality, so they have no uncovered label to decide.
    /// </summary>
    public static class QualityGate
    {
        /// <summary>
        /// Whether <paramref name="profile"/> permits <paramref name="qualityLabel"/>.
        ///
        /// Three rules, in order.
        ///
        /// A name match wins outright: the label and a rung name, either of which may be the
        /// broader, so the bare "MP3" a loose parser emits lands on "MP3 320kbps".
        ///
        /// Only when no rung name matches does the codec group decide, which is what lets a profile
        /// refuse a container label like "M4B" that names no rung of its own. "M4A", "MP4", "AAX"
        /// and "AAXC" are MPEG-4 containers and land in the AAC group the same way. Note what that
        /// ordering costs. Once the fallback is reached the gate is working at codec granularity,
        /// and one permitted rung in the group permits the whole group. A profile that refuses
        /// "MP3 VBR" refuses a release labelled exactly that, and permits one labelled "MP3 V0" as
        /// long as some other MP3 rung is permitted. Per-rung refusal only bites on labels the
        /// ladder names.
        ///
        /// When no rung covers the label, either because the ladder has no rung in its codec group
        /// or because <see cref="QualityMatcher.CodecGroupOfLabel"/> cannot say what codec it is,
        /// the gate decides the way the old allow-list did. That list was the allowed rung names
        /// plus every PreferredFormats token, so with no rung to match, a token naming the label
        /// was the only way through. Where a token names it the gate returns
        /// <see cref="QualityGateVerdict.NoOpinion"/>; otherwise
        /// <see cref="QualityGateVerdict.Refused"/>. It can only keep a release the old code kept,
        /// and it never overrides a rung's Allowed flag. "Lossless" and a bare bitrate are among the
        /// labels with no codec group: ParseQualityLabel recognises both but has no codec to report.
        ///
        /// The stock default profile keeps the domain's PreferredFormats (m4b, mp3, m4a, flac,
        /// opus) and QualityProfileService.EnsureProfileHasRequiredQualitiesAsync keeps its ladder
        /// to the eleven AAC and MP3 rungs, so FLAC and OPUS stay permitted there. A profile saved
        /// from the settings UI stores PreferredFormats as [] or ["m4b"], and switching a codec off
        /// deletes that codec's rungs, so FLAC and OPUS stay refused there. Making the ladder
        /// exhaustive, the way Sonarr and Readarr do, would let this read the Allowed flag instead,
        /// and is a larger change.
        ///
        /// Reachability, stated so nobody has to re-derive it: no parser in this repo emits AAX,
        /// AAXC, MP4 or M4A as a quality or a format today. Every provider maps format into a
        /// closed set first. The one line that would pass a raw filetype through, in
        /// MyAnonamouseSearchProvider's enrichment, runs only when Format is empty, and the
        /// MyAnonamouse parser always fills Format (DetectFormatFromTags falls back to "MP3"). So
        /// how those labels are judged is the scorer's contract for the first parser that emits
        /// one. Under it they follow the AAC rungs: refused by a profile that refuses every AAC
        /// rung, and permitted wherever an AAC rung is allowed, the stock default included. The
        /// allow-list this replaced refused AAX, AAXC and MP4 on every profile with a ladder.
        /// </summary>
        public static QualityGateVerdict Evaluate(string? qualityLabel, QualityProfile? profile)
        {
            if (string.IsNullOrWhiteSpace(qualityLabel))
            {
                return QualityGateVerdict.NoOpinion;
            }

            var rungs = profile?.Qualities?
                .Where(rung => rung != null && !string.IsNullOrWhiteSpace(rung.Quality))
                .ToList();

            if (rungs == null || rungs.Count == 0)
            {
                return QualityGateVerdict.NoOpinion;
            }

            var label = qualityLabel.Trim().ToLowerInvariant();

            var named = rungs
                .Where(rung => NamesTheSameQuality(label, rung.Quality.Trim().ToLowerInvariant()))
                .ToList();

            if (named.Count > 0)
            {
                return Verdict(named);
            }

            var group = QualityMatcher.CodecGroupOfLabel(qualityLabel);
            if (group == null)
            {
                // No rung names it and nothing can say what codec it is, so the third rule above
                // decides. A bare bitrate has no codec group either and reaches here only when no
                // rung name carries that bitrate, so "320kbps" is decided by rule 1 against the
                // seeded ladder while "96kbps" is decided here. That is an accident of which
                // bitrates the seed lists rather than a designed boundary, and it matches what the
                // allow-list this replaced did with the same two labels.
                return NoRungCovers(label, profile!);
            }

            var peers = rungs
                .Where(rung => string.Equals(QualityMatcher.CodecGroupOfRung(rung), group, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return peers.Count == 0 ? NoRungCovers(label, profile!) : Verdict(peers);
        }

        /// <summary>
        /// No rung covers the label. The old allow-list still let it through when a
        /// PreferredFormats token named it, by the same two-way substring test, and refused it
        /// otherwise; this keeps both answers.
        /// </summary>
        private static QualityGateVerdict NoRungCovers(string label, QualityProfile profile)
            => (profile.PreferredFormats ?? new List<string>())
                .Where(format => !string.IsNullOrWhiteSpace(format))
                .Select(format => format.Trim().ToLowerInvariant())
                .Any(format => NamesTheSameQuality(label, format))
                ? QualityGateVerdict.NoOpinion
                : QualityGateVerdict.Refused;

        /// <summary>
        /// Whether the profile refuses this label outright. A label it says nothing about is not
        /// a refusal.
        /// </summary>
        public static bool Refuses(string? qualityLabel, QualityProfile? profile)
            => Evaluate(qualityLabel, profile) == QualityGateVerdict.Refused;

        /// <summary>
        /// One permitted rung among those covering the label is enough. Where a broad label such
        /// as "MP3" covers several rungs the release could be any of them, so the permissive
        /// reading is the honest one.
        /// </summary>
        private static QualityGateVerdict Verdict(List<QualityDefinition> covering)
            => covering.Any(rung => rung.Allowed) ? QualityGateVerdict.Allowed : QualityGateVerdict.Refused;

        private static bool NamesTheSameQuality(string label, string rung)
            => label.Length > 0
               && rung.Length > 0
               && (label.Contains(rung, StringComparison.Ordinal) || rung.Contains(label, StringComparison.Ordinal));
    }
}
