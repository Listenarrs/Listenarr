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

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Groups a record's files into per-book clusters for the "Split
    /// collection" workflow (a multi-book dump imported onto one record —
    /// live case: 773 files spanning 34 books). Subdirectory is the strongest
    /// grouping signal; root-level files cluster by their filename with track
    /// numbering and noise stripped, so "friday-01_77.mp3" … "friday-44_77.mp3"
    /// form one "friday" cluster. Pure and deterministic for unit testing.
    /// </summary>
    public static class FileClustering
    {
        public sealed record FileCluster(string Key, string DisplayName, List<AudiobookFile> Files);

        // Trailing track/part markers: "-01_77", " Part 03 of 26", "_42",
        // " - 7 - 7", "(2 of 3)", and bare parenthesized/bracketed track
        // numbers "(10)" / "[07]" (live case: "The Rolling Stones (1)" …
        // "(50)" exploded into 50 singleton groups without it).
        private static readonly Regex TrailingNumberingRegex = new(
            @"(\s*[-_ ]\s*(part|pt|cd|disc|disk|track|chapter|ch)?\s*\d+(\s*(of|/)\s*\d+)?|\s*[(\[]\d+(\s*(of|/)\s*\d+)?[)\]]|[-_ ]\d+([_-]\d+)*)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Leading list numbering: "1 The Year of the Jackpot".
        private static readonly Regex LeadingNumberRegex = new(@"^\d{1,3}[\s.\-_]+", RegexOptions.Compiled);

        // A trailing volume/book designator ("…, Vol. 2", "Book 3") whose own
        // number must NOT be mistaken for track numbering — otherwise
        // "Expanded Universe, Vol. 1-001" and "…Vol. 2-001" both reduce to
        // "…, Vol." with the same signature and collapse into one cluster, so a
        // multi-volume set dumped onto one record can't be split apart.
        private static readonly Regex DanglingVolumeRegex = new(
            @"\b(vol|volume|book|bk)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Leading number of a matched trailing run ("  1" of "  1-001").
        private static readonly Regex LeadingRunNumberRegex = new(@"^\s*\d+", RegexOptions.Compiled);

        public static List<FileCluster> Cluster(
            IEnumerable<AudiobookFile> files,
            string? basePath,
            IReadOnlyDictionary<int, string>? embeddedTitles = null)
        {
            var groups = new Dictionary<string, FileCluster>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files.Where(f => !string.IsNullOrWhiteSpace(f.Path)))
            {
                var relative = MakeRelative(file.Path!, basePath);
                var slash = relative.IndexOf('/');

                string key;
                string display;
                if (slash > 0)
                {
                    // Subdirectory wins — even over the embedded tag. A per-book subfolder is the
                    // strongest grouping signal, and trusting the tag over it merged genuinely
                    // different books that shared a (mis-)tag across folders — e.g. a
                    // "[Dramatized Adaptation]" subfolder lumped with a novel because both carried
                    // the series album "Throne of Glass bk 2", and a two-file book split apart
                    // because its files were tagged inconsistently.
                    key = "dir:" + relative[..slash];
                    display = relative[..slash];
                }
                else if (embeddedTitles != null
                    && embeddedTitles.TryGetValue(file.Id, out var embeddedTitle)
                    && TryEmbeddedTitleKey(embeddedTitle, out var embedKey, out var embedDisplay))
                {
                    // Flat files only: when a collection was bulk-renamed to the parent record's
                    // name, every filename is identical and useless for splitting — but each
                    // file's embedded Album/Title tag still names the real book (e.g. files all
                    // named "Old Man's War-NNN.mp3" whose tags say "The Ghost Brigades" /
                    // "The Sagan Diary"). Track noise is stripped the same way as filenames; the
                    // numbering *style* is intentionally ignored (a tag is a book identity).
                    key = embedKey;
                    display = embedDisplay;
                }
                else
                {
                    var (stem, signature) = CleanStemWithSignature(Path.GetFileNameWithoutExtension(relative));
                    // The numbering STYLE is part of the identity: a record
                    // holding two copies of one book ("Title-NN.mp3" and
                    // "Title (N).mp3") must yield two groups, or the
                    // delete-the-duplicate-copy workflow can't target one.
                    key = "stem:" + stem.ToLowerInvariant() + "|" + signature;
                    display = stem;
                }

                if (!groups.TryGetValue(key, out var cluster))
                {
                    cluster = new FileCluster(key, display, new List<AudiobookFile>());
                    groups[key] = cluster;
                }
                cluster.Files.Add(file);
            }

            MergeUnnumberedSiblings(groups);

            return groups.Values
                .OrderByDescending(c => c.Files.Count)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Builds a cluster key/display from a file's embedded book title, stripping the same
        /// track/list numbering noise as filenames ("The Ghost Brigades 10-41" → "The Ghost
        /// Brigades"; "1 - The Sagan Diary" → "The Sagan Diary"). Returns false when the tag
        /// carries no usable signal (empty, or cleans down to nothing/bare digits) so the caller
        /// falls back to path-based clustering for that file.
        /// </summary>
        private static bool TryEmbeddedTitleKey(string? embeddedTitle, out string key, out string display)
        {
            key = string.Empty;
            display = string.Empty;
            if (string.IsNullOrWhiteSpace(embeddedTitle))
            {
                return false;
            }

            var (stem, _) = CleanStemWithSignature(embeddedTitle);
            if (string.IsNullOrWhiteSpace(stem) || stem.All(char.IsDigit))
            {
                return false;
            }

            key = "embed:" + stem.ToLowerInvariant();
            display = stem;
            return true;
        }

        /// <summary>
        /// An unnumbered file alongside a numbered set ("Title.mp3" next to
        /// "Title (1).mp3"…) is usually that set's FIRST track — numbering
        /// often starts at the unmarked file (users have resorted to renaming
        /// it "Title (0).mp3" by hand). Merge it into the same-stem numbered
        /// group when it plausibly IS one track (duration/size comparable to
        /// the group's median); a same-stem whole-book file is many times
        /// larger and stays its own group, as does anything ambiguous
        /// (multiple same-stem numbered groups to choose from).
        /// </summary>
        private static void MergeUnnumberedSiblings(Dictionary<string, FileCluster> groups)
        {
            var markerless = groups
                .Where(kv => kv.Key.StartsWith("stem:", StringComparison.Ordinal)
                             && kv.Key.EndsWith("|", StringComparison.Ordinal)
                             && kv.Value.Files.Count == 1)
                .ToList();

            foreach (var (key, cluster) in markerless)
            {
                // The markerless key "stem:<stem>|" is itself the shared prefix
                // of every same-stem numbered group ("stem:<stem>|<signature>").
                var candidates = groups
                    .Where(kv => kv.Key != key
                                 && kv.Key.StartsWith(key, StringComparison.Ordinal)
                                 && kv.Value.Files.Count > 0)
                    .Select(kv => kv.Value)
                    .ToList();
                if (candidates.Count != 1) continue; // none, or ambiguous (two copies)

                var target = candidates[0];
                if (!LooksLikeOneTrackOf(cluster.Files[0], target.Files)) continue;

                target.Files.Insert(0, cluster.Files[0]);
                groups.Remove(key);
            }
        }

        private static bool LooksLikeOneTrackOf(AudiobookFile file, List<AudiobookFile> groupFiles)
        {
            const double MaxRatio = 3.0;

            var fileDuration = file.DurationSeconds ?? 0;
            var durations = groupFiles.Select(f => f.DurationSeconds ?? 0).Where(d => d > 0).OrderBy(d => d).ToList();
            if (fileDuration > 0 && durations.Count > 0)
            {
                return fileDuration <= durations[durations.Count / 2] * MaxRatio;
            }

            var fileSize = file.Size ?? 0;
            var sizes = groupFiles.Select(f => f.Size ?? 0).Where(s => s > 0).OrderBy(s => s).ToList();
            if (fileSize > 0 && sizes.Count > 0)
            {
                return fileSize <= sizes[sizes.Count / 2] * MaxRatio;
            }

            // No comparable signal — stay conservative, keep it separate.
            return false;
        }

        /// <summary>
        /// Strips track numbering and list prefixes so sibling files share a
        /// stem: "(heinlein_robert)-friday-01_77" → "(heinlein_robert)-friday".
        /// Applied repeatedly because markers stack ("Part 01 of 26" + "_77").
        /// </summary>
        public static string CleanStem(string fileName) => CleanStemWithSignature(fileName).Stem;

        /// <summary>
        /// As <see cref="CleanStem"/>, plus the digit-normalized template of
        /// what was stripped ("-#_#", " (#)", " part # of #") — the marker
        /// STYLE that distinguishes two copies of the same book.
        /// </summary>
        public static (string Stem, string Signature) CleanStemWithSignature(string fileName)
        {
            var stem = fileName.Trim();
            var signatureParts = new List<string>();

            var leading = LeadingNumberRegex.Match(stem);
            if (leading.Success)
            {
                signatureParts.Add(NormalizeDigits(leading.Value));
                stem = stem[leading.Length..];
            }

            string previous;
            do
            {
                previous = stem;
                var trailing = TrailingNumberingRegex.Match(stem);
                if (!trailing.Success) break;

                var stripIndex = trailing.Index;
                var markerValue = trailing.Value;

                // Volume/book guard: when "<title>, Vol. N" is followed by a
                // track marker, the matched run is " N-001"; keep "Vol. N" on
                // the stem (strip only the track part) so different volumes of
                // one title don't collapse into a single cluster.
                var runNumber = LeadingRunNumberRegex.Match(markerValue);
                if (runNumber.Success && DanglingVolumeRegex.IsMatch(stem[..stripIndex]))
                {
                    // The run is only the volume's own number ("…, Vol. 2") —
                    // keep it whole as part of the identity, strip nothing more.
                    if (runNumber.Length >= markerValue.Length) break;

                    // Keep "<keyword> N"; the remainder is the real track marker.
                    stripIndex += runNumber.Length;
                    markerValue = stem[stripIndex..];
                }

                // Outermost marker first so stacked markers serialize stably.
                signatureParts.Insert(leading.Success ? 1 : 0, NormalizeDigits(markerValue));
                stem = stem[..stripIndex].Trim();
            } while (stem != previous && stem.Length > 0);

            // A stem that stripped down to nothing or to bare digits carries no
            // grouping signal — keep the original name (conservative: such
            // files stay singleton clusters instead of merging on noise).
            if (stem.Length == 0 || stem.All(char.IsDigit)) return (fileName.Trim(), string.Empty);
            return (stem, string.Join("", signatureParts));
        }

        private static string NormalizeDigits(string marker) =>
            Regex.Replace(marker.Trim().ToLowerInvariant(), @"\d+", "#");

        private static string MakeRelative(string path, string? basePath)
        {
            var normalized = path.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                var root = basePath.Replace('\\', '/').TrimEnd('/') + "/";
                if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return normalized[root.Length..];
                }
            }
            // Fall back to the file name so an unanchored path can't smuggle in
            // its whole directory chain as a "subdirectory" cluster.
            return Path.GetFileName(normalized);
        }
    }
}
