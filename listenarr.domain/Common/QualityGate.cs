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

        /// <summary>The profile carries a rung covering this quality and refuses it.</summary>
        Refused,

        /// <summary>
        /// The profile describes nothing that covers this quality: there is no label to judge,
        /// or no ladder to judge it against, or the label names a codec the ladder carries no
        /// rung for. Those are the absences that read as silence. The absence that does not is a
        /// label the gate cannot place at all, which is <see cref="Refused"/>.
        /// </summary>
        NoOpinion
    }

    /// <summary>
    /// Reads a quality profile's <see cref="QualityDefinition.Allowed"/> flags as a gate, and
    /// nothing else as a gate.
    ///
    /// A profile expresses two separate things about a release. Its quality rungs say what is
    /// permitted; its <see cref="QualityProfile.PreferredFormats"/> say what is preferred among
    /// the permitted. Only the first belongs here. The scorer applies the second as a score
    /// adjustment, which is where a preference belongs and where it cannot widen the gate.
    /// Sonarr and Readarr keep the same two apart: their QualityAllowedByProfileSpecification is
    /// a boolean veto that no custom-format score can override.
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
        /// refuse a container label like "M4B" that names no rung of its own. Note what that
        /// ordering costs. Once the fallback is reached the gate is working at codec granularity,
        /// and one permitted rung in the group permits the whole group. A profile that refuses
        /// "MP3 VBR" refuses a release labelled exactly that, and permits one labelled "MP3 V0" as
        /// long as some other MP3 rung is permitted. Per-rung refusal only bites on labels the
        /// ladder names.
        ///
        /// The third rule is for a label the gate cannot place at all: no rung names it, and
        /// <see cref="QualityMatcher.CodecGroupOfLabel"/> cannot say what codec it is. Read that
        /// literally, because it is narrower than "unrecognised". "Lossless" is recognised by
        /// ParseQualityLabel, which reports it as lossless with no codec, and a bare bitrate is
        /// recognised as a bitrate with no codec; both have no codec to report and so both land
        /// here. That is
        /// <see cref="QualityGateVerdict.Refused"/>. A ladder is an allow-list, and a label nobody
        /// can map onto it is not on it. The argument for the other reading is that absence is not
        /// refusal, and it is a good argument about a *rung*, but it does not reach this far: the
        /// question here is not whether the operator ticked a box, it is that nothing in the
        /// profile is even about this release. The costs are not symmetric either. A wrongly
        /// refused release is one candidate missing from a list, carrying its reason. A wrongly
        /// permitted one is a grab, and it wins the ranking on the way through, because the labels
        /// in this gap are containers the scorer ranks highly: AAX is 95, second only to FLAC
        /// (SearchResultScorer.GetQualityScore).
        ///
        /// Reachability, stated so nobody has to re-derive it: no parser in this repo emits AAX,
        /// AAXC or MP4 as a quality or a format today. Every provider maps format into a closed
        /// set first. The one line that would pass a raw filetype through, in
        /// MyAnonamouseSearchProvider's enrichment, runs only when Format is empty, and the
        /// MyAnonamouse parser always fills Format (DetectFormatFromTags falls back to "MP3").
        /// So this rule is the scorer's contract, ready for the first parser that does emit one,
        /// not a fix for an observed grab. The scorer's aax branch would rank such a release highly.
        ///
        /// Two things this is NOT. It is not new severity. The scorer used to build this allow-list
        /// inline from the Allowed=true rungs and refuse any label that matched none of them, so
        /// every profile carrying a ladder already refused AAX, the stock default included. An
        /// earlier draft of this comment implied otherwise; it was wrong, and dropping the third
        /// rule would have been a loosening rather than a preserved behaviour.
        ///
        /// And it is not the second rule in disguise: a label that IS placed, into a codec group
        /// the ladder happens to carry no rung for, still returns
        /// <see cref="QualityGateVerdict.NoOpinion"/>. That case has to stay silent.
        /// QualityProfileService.EnsureProfileHasRequiredQualitiesAsync re-adds any of eleven AAC
        /// and MP3 rungs missing from the default profile, with Allowed set to true, on every read
        /// of it. Deletion is undone; Allowed=false survives. Reading that absence as refusal would
        /// refuse FLAC, OPUS and every other codec that seeded ladder never lists, on the stock
        /// default profile, while doing nothing an operator asked for.
        ///
        /// The cost that remains: a codec switched off in the settings UI has its rungs deleted
        /// rather than kept as not-allowed, so codec-level refusal made that way is still not
        /// honoured here. Making the ladder exhaustive, the way Sonarr and Readarr do, is the fix
        /// and is a larger change than this one.
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
                // No rung names it and nothing can say what codec it is. Refused, per the third
                // rule above. A bare bitrate has no codec group either and reaches here only when
                // no rung name carries that bitrate, so "320kbps" is decided by rule 1 against the
                // seeded ladder while "96kbps" is refused here. That is an accident of which
                // bitrates the seed lists rather than a designed boundary, and it matches what the
                // allow-list this replaced did with the same two labels.
                return QualityGateVerdict.Refused;
            }

            var peers = rungs
                .Where(rung => string.Equals(QualityMatcher.CodecGroupOfRung(rung), group, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return peers.Count == 0 ? QualityGateVerdict.NoOpinion : Verdict(peers);
        }

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
