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

namespace Listenarr.Application.Audiobooks.Catalog
{
    // Lists the books whose Audible identity is incomplete: no title, no ASIN, an ASIN that isn't
    // one, or an ASIN without the region (catalogue) it belongs to. Repair fixes them.
    public class LibraryIssueService : ILibraryIssueService
    {
        private readonly IAudiobookRepository _audiobookRepository;

        public LibraryIssueService(IAudiobookRepository audiobookRepository)
        {
            _audiobookRepository = audiobookRepository;
        }

        public async Task<IReadOnlyList<LibraryIssueItem>> GetIssuesAsync(CancellationToken ct = default)
        {
            // Sequential: both reads use the same scoped DbContext
            var audiobooks = await _audiobookRepository.GetAllAsync();
            var asinsByAudiobookId = await _audiobookRepository.GetAsinIdentifiersGroupedByAudiobookIdAsync(ct);

            var items = new List<LibraryIssueItem>();
            foreach (var audiobook in audiobooks.OrderBy(a => a.Title))
            {
                asinsByAudiobookId.TryGetValue(audiobook.Id, out var asins);
                var primary = asins?.FirstOrDefault();
                var asin = primary?.ValueNormalized is { Length: > 0 } normalized
                    ? normalized
                    : audiobook.Asin?.Trim();
                var issues = FindIssues(audiobook.Title, asin, primary);
                if (issues.Count == 0)
                {
                    continue;
                }

                items.Add(new LibraryIssueItem
                {
                    Id = audiobook.Id,
                    Title = audiobook.Title,
                    Authors = audiobook.Authors?.ToArray(),
                    Series = audiobook.Series,
                    SeriesNumber = audiobook.SeriesNumber,
                    ImageUrl = audiobook.ImageUrl,
                    Asin = string.IsNullOrWhiteSpace(asin) ? null : asin,
                    Region = string.IsNullOrWhiteSpace(primary?.Region) ? null : primary!.Region,
                    Issues = issues.ToArray()
                });
            }

            return items;
        }

        private static List<string> FindIssues(string? title, string? asin, AudiobookExternalIdentifier? primary)
        {
            var issues = new List<string>();
            if (string.IsNullOrWhiteSpace(title))
            {
                issues.Add(LibraryIssueCodes.MissingTitle);
            }

            if (string.IsNullOrWhiteSpace(asin))
            {
                issues.Add(LibraryIssueCodes.MissingAsin);
            }
            else if (primary == null && !IsWellFormedAsin(asin))
            {
                // Only the book's own ASIN field is set, and it isn't an ASIN (e.g. typed by hand)
                issues.Add(LibraryIssueCodes.InvalidAsin);
            }
            else if (string.IsNullOrWhiteSpace(primary?.Region))
            {
                // No catalogue recorded: rescans fall back to US, then UK
                issues.Add(LibraryIssueCodes.MissingRegion);
            }

            return issues;
        }

        private static bool IsWellFormedAsin(string asin) =>
            asin.Length == 10 && asin.All(char.IsAsciiLetterOrDigit);
    }
}
