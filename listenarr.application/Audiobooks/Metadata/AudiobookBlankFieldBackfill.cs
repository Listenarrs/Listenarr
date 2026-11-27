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
namespace Listenarr.Application.Audiobooks.Metadata
{
    /// <summary>
    /// Fills the fields an audiobook record LACKS from provider metadata and
    /// leaves every populated field alone. This is the "fill missing" half of the
    /// metadata rescan: the rescan overwrites with the provider's answer, which is
    /// right for an explicit user refresh but wrong for an unattended worker that
    /// must never clobber manual edits or a verified title. Identifiers (ASIN,
    /// ISBN, OpenLibrary) and the cover are deliberately out of scope here — the
    /// cover needs the image cache and identifiers need the identifier mapper, so
    /// callers handle both around this call.
    /// </summary>
    public static class AudiobookBlankFieldBackfill
    {
        /// <summary>
        /// Copies each blank scalar/list field from <paramref name="metadata"/> onto
        /// <paramref name="audiobook"/>. Returns the names of the fields that were
        /// filled (empty when the record already had everything the provider knows).
        /// </summary>
        public static IReadOnlyList<string> Apply(Audiobook audiobook, AudibleBookMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(audiobook);
            ArgumentNullException.ThrowIfNull(metadata);

            var filled = new List<string>();

            FillText(filled, nameof(Audiobook.Title), audiobook.Title, metadata.Title, v => audiobook.Title = v);
            FillText(filled, nameof(Audiobook.Subtitle), audiobook.Subtitle, metadata.Subtitle, v => audiobook.Subtitle = v);
            FillText(filled, nameof(Audiobook.Description), audiobook.Description, metadata.Description, v => audiobook.Description = v);
            FillText(filled, nameof(Audiobook.Publisher), audiobook.Publisher, metadata.Publisher, v => audiobook.Publisher = v);
            FillText(filled, nameof(Audiobook.Language), audiobook.Language, metadata.Language, v => audiobook.Language = v);
            FillText(filled, nameof(Audiobook.PublishYear), audiobook.PublishYear, metadata.PublishYear, v => audiobook.PublishYear = v);
            FillText(filled, nameof(Audiobook.PublishedDate), audiobook.PublishedDate, metadata.PublishedDate, v => audiobook.PublishedDate = v);
            FillText(filled, nameof(Audiobook.Version), audiobook.Version, metadata.Version, v => audiobook.Version = v);

            if ((audiobook.Runtime is null || audiobook.Runtime <= 0) && metadata.Runtime is > 0)
            {
                audiobook.Runtime = metadata.Runtime;
                filled.Add(nameof(Audiobook.Runtime));
            }

            FillList(
                filled,
                nameof(Audiobook.Authors),
                audiobook.Authors,
                FirstNonEmptyList(metadata.Authors, metadata.Author),
                v => audiobook.Authors = v);
            FillList(
                filled,
                nameof(Audiobook.Narrators),
                audiobook.Narrators,
                FirstNonEmptyList(metadata.Narrators, metadata.Narrator),
                v => audiobook.Narrators = v);
            FillList(filled, nameof(Audiobook.Genres), audiobook.Genres, Normalize(metadata.Genres), v => audiobook.Genres = v);
            FillList(filled, nameof(Audiobook.Tags), audiobook.Tags, Normalize(metadata.Tags), v => audiobook.Tags = v);

            // Series: only when the record has no series at all. A book already
            // placed in a series (possibly by hand) keeps its placement untouched.
            var hasSeries = (audiobook.SeriesMemberships?.Any(m => !string.IsNullOrWhiteSpace(m.SeriesName)) ?? false)
                || !string.IsNullOrWhiteSpace(audiobook.Series);
            var providerHasSeries = (metadata.SeriesMemberships?.Any(m => !string.IsNullOrWhiteSpace(m.SeriesName)) ?? false)
                || !string.IsNullOrWhiteSpace(metadata.Series);
            if (!hasSeries && providerHasSeries)
            {
                AudiobookSeriesMembershipHelper.ApplyToAudiobook(
                    audiobook,
                    metadata.SeriesMemberships,
                    metadata.Series,
                    metadata.SeriesNumber);
                filled.Add(nameof(Audiobook.Series));
            }

            return filled;
        }

        private static void FillText(
            List<string> filled,
            string name,
            string? current,
            string? incoming,
            Action<string> assign)
        {
            if (!string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(incoming))
            {
                return;
            }

            assign(incoming.Trim());
            filled.Add(name);
        }

        private static void FillList(
            List<string> filled,
            string name,
            List<string>? current,
            List<string> incoming,
            Action<List<string>> assign)
        {
            if ((current?.Any(v => !string.IsNullOrWhiteSpace(v)) ?? false) || incoming.Count == 0)
            {
                return;
            }

            assign(incoming);
            filled.Add(name);
        }

        private static List<string> FirstNonEmptyList(IEnumerable<string>? list, string? single)
        {
            var normalized = Normalize(list);
            if (normalized.Count > 0)
            {
                return normalized;
            }

            return string.IsNullOrWhiteSpace(single)
                ? new List<string>()
                : new List<string> { single.Trim() };
        }

        private static List<string> Normalize(IEnumerable<string>? values) =>
            (values ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
