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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Listenarr.Application.Search.Matching;

public sealed record BookMatchContext(
    IReadOnlyList<string> Titles, IReadOnlyList<string> Authors, string? Asin, string Fingerprint)
{
    public IReadOnlyList<string> Isbn { get; init; } = [];

    public static BookMatchContext From(Audiobook book, QualityProfile? profile = null)
    {
        var titles = new List<string>();
        var title = IdentityText.Canonical(book.Title);
        if (title.Length > 0) titles.Add(title);
        var subtitle = IdentityText.Canonical(book.Subtitle);
        if (title.Length > 0 && subtitle.Length > 0)
        {
            // Only a separately supplied subtitle establishes this field relationship.
            var mainTitle = title;
            if (title.EndsWith(subtitle, StringComparison.OrdinalIgnoreCase))
            {
                var prefix = title[..^subtitle.Length].TrimEnd();
                if (prefix.EndsWith(':')) mainTitle = prefix[..^1].TrimEnd();
            }
            if (mainTitle.Length > 0)
            {
                titles.Add(mainTitle);
                titles.Add(mainTitle + ": " + subtitle);
                titles.Add(mainTitle + ":" + subtitle);
            }
        }
        var authors = (book.Authors ?? []).Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(IdentityText.Canonical).Where(IdentityText.IsContributorName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var p = profile ?? book.QualityProfile;
        var payload = JsonSerializer.Serialize(new
        {
            book.Id,
            book.Title,
            book.Subtitle,
            Authors = authors,
            book.Asin,
            book.Isbn,
            book.Series,
            book.SeriesNumber,
            Profile = p == null ? null : new
            {
                p.Id,
                p.Qualities,
                p.CutoffQuality,
                p.MinimumSize,
                p.MaximumSize,
                p.PreferredFormats,
                p.PreferredWords,
                p.MustContain,
                p.MustNotContain,
                p.PreferredLanguages,
                p.MinimumSeeders,
                p.MinimumScore,
                p.PreferNewerReleases,
                p.MaximumAge
            },
            Policy = AutomaticSearchAssessment.PolicyVersion
        });
        return new(titles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), authors, book.Asin,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))))
        { Isbn = book.Isbn?.ToArray() ?? [] };
    }
}
