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
    private IReadOnlyList<string> _narrators = [];
    internal NarratorNameCatalog NarratorCatalog { get; private init; } = NarratorNameCatalog.Empty;
    public IReadOnlyList<string> Narrators
    {
        get => _narrators;
        init
        {
            _narrators = Array.AsReadOnly(value.ToArray());
            NarratorCatalog = _narrators.Count == 0 ? NarratorNameCatalog.Empty : new(_narrators, _annotationCatalog);
        }
    }
    private IReadOnlyList<ReleaseSeriesReference> _seriesReferences = [];
    internal ReleaseSeriesCatalog SeriesCatalog { get; private init; } = ReleaseSeriesCatalog.Empty;
    private ReleaseAnnotationCatalog _annotationCatalog = ReleaseAnnotationCatalog.Unavailable;
    public ReleaseAnnotationCatalog AnnotationCatalog
    {
        get => _annotationCatalog;
        init
        {
            _annotationCatalog = value ?? ReleaseAnnotationCatalog.Unavailable;
            SeriesCatalog = _seriesReferences.Count == 0 ? ReleaseSeriesCatalog.Empty : new(_seriesReferences, _annotationCatalog);
            NarratorCatalog = _narrators.Count == 0 ? NarratorNameCatalog.Empty : new(_narrators, _annotationCatalog);
        }
    }
    public IReadOnlyList<ReleaseSeriesReference> SeriesReferences
    {
        get => _seriesReferences;
        init
        {
            _seriesReferences = Array.AsReadOnly(value.ToArray());
            SeriesCatalog = _seriesReferences.Count == 0 ? ReleaseSeriesCatalog.Empty : new(_seriesReferences, _annotationCatalog);
        }
    }
    public ReferenceTitleParts? TitleParts { get; init; }
    public string? Language { get; init; }
    public IReadOnlyList<string> PreferredLanguages { get; init; } = [];
    public IReadOnlyList<string> Editions { get; init; } = [];

    public static BookMatchContext From(Audiobook book, QualityProfile? profile = null, ReleaseAnnotationCatalog? catalog = null)
    {
        catalog ??= ReleaseAnnotationCatalog.Unavailable;
        if (book.Authors?.Count > 64 || book.Narrators?.Count > 64 || book.SeriesMemberships?.Count > 64
            || book.Language?.Length > 256 || (profile ?? book.QualityProfile)?.PreferredLanguages?.Count > 64)
            throw new ArgumentException("Reference facts exceed assessment budget");
        var titles = new List<string>();
        var title = IdentityText.Canonical(book.Title);
        if (title.Length > 0) titles.Add(title);
        var subtitle = IdentityText.Canonical(book.Subtitle);
        ReferenceTitleParts? titleParts = null;
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
                titleParts = new(mainTitle, subtitle);
                titles.Add(mainTitle);
                titles.Add(mainTitle + ": " + subtitle);
                titles.Add(mainTitle + ":" + subtitle);
            }
        }
        var authors = (book.Authors ?? []).Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(IdentityText.Canonical).Where(IdentityText.IsContributorName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var narrators = (book.Narrators ?? []).Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(IdentityText.Canonical).Where(IdentityText.IsContributorName)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var subtitleSeries = ReleaseAnnotationGrammar.SeriesFromSubtitle(subtitle);
        var series = (book.SeriesMemberships ?? []).Select(s => new ReleaseSeriesReference(
                IdentityText.Canonical(s.SeriesName), IdentityText.Canonical(s.SeriesNumber)))
            .Append(new(IdentityText.Canonical(book.Series), IdentityText.Canonical(book.SeriesNumber)))
            .Concat(subtitleSeries is null ? [] : new[] { subtitleSeries })
            .Where(s => s.Name.Length > 0).Distinct()
            .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Position, StringComparer.Ordinal).ToArray();
        var p = profile ?? book.QualityProfile;
        var payload = JsonSerializer.Serialize(new
        {
            book.Id,
            book.Title,
            book.Subtitle,
            book.Language,
            TitleParts = titleParts,
            Authors = authors,
            book.Asin,
            book.Isbn,
            book.Series,
            book.SeriesNumber,
            Narrators = narrators,
            SeriesReferences = series,
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
            Policy = AutomaticSearchAssessment.PolicyVersion,
            Catalog = catalog.Fingerprint
        });
        return new(titles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), authors, book.Asin,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))))
        {
            AnnotationCatalog = catalog,
            Isbn = book.Isbn?.ToArray() ?? [],
            Narrators = narrators,
            SeriesReferences = series,
            TitleParts = titleParts,
            Language = book.Language,
            PreferredLanguages = Array.AsReadOnly((p?.PreferredLanguages ?? []).ToArray()),
            Editions = Array.AsReadOnly(EditionCompatibilityPolicy.Parse(book.Subtitle)
                .Concat(EditionCompatibilityPolicy.FromTitle(book.Title)).Distinct().ToArray())
        };
    }
}
