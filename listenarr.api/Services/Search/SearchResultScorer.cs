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
namespace Listenarr.Api.Services.Search;

/// <summary>
/// Computes relevance scores for search results based on multiple factors.
/// </summary>
public class SearchResultScorer
{
    private readonly ILogger<SearchResultScorer> _logger;

    // Scoring weights - tuned to prefer title containment and author match
    private const double W_TitleContainment = 0.45;
    private const double W_AuthorMatch = 0.18;
    private const double W_TitleFuzzy = 0.12;
    private const double W_Completeness = 0.10;
    private const double W_Source = 0.06;
    private const double W_AsinExact = 0.05;
    private const double W_Series = 0.04;

    public SearchResultScorer(ILogger<SearchResultScorer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Computes a comprehensive relevance score for a search result.
    /// </summary>
    public ScoredSearchResult ScoreResult(
        SearchResult result,
        string query,
        double containmentScore,
        double fuzzyScore)
    {
        // Always preserve OpenLibrary-sourced items
        var isOpenLibrary = string.Equals(result.MetadataSource, "OpenLibrary", StringComparison.OrdinalIgnoreCase);

        // Compute completeness: fraction of important fields present
        int fieldsPresent = 0;
        if (!string.IsNullOrWhiteSpace(result.Title)) fieldsPresent++;
        if (!string.IsNullOrWhiteSpace(result.Artist)) fieldsPresent++;
        if (!string.IsNullOrWhiteSpace(result.Publisher)) fieldsPresent++;
        if (!string.IsNullOrWhiteSpace(result.ImageUrl)) fieldsPresent++;
        double completenessScore = fieldsPresent / 4.0;

        // Source priority: proper metadata sources get higher base multiplier
        int sourcePriority = 0;
        if (!string.IsNullOrEmpty(result.MetadataSource))
        {
            var md = result.MetadataSource.ToLowerInvariant();
            if (md.Contains("audible") || md.Contains("audnex") || md.Contains("audnexus") || md.Contains("openlibrary"))
                sourcePriority = 2;
            else if (md == "amazon" || md == "audible")
                sourcePriority = 1;
            else
                sourcePriority = 1;
        }

        // Title containment (primary signal)
        double titleContainment = containmentScore; // 0.0 - 1.0

        // Title fuzzy similarity (near-miss spellings)
        double titleFuzzy = fuzzyScore; // 0.0 - 1.0

        // Author match: proportion of query tokens that appear in artist
        double authorMatch = ComputeAuthorMatch(result.Artist, query);

        // ASIN exact match: if the query is exactly an ASIN, strong boost
        double asinMatch = ComputeAsinMatch(result.Asin, query);

        // Series match: give small boost if series tokens found
        double seriesMatch = ComputeSeriesMatch(result.Series, query);

        // Source priority normalized to 0..1
        double sourcePriorityNormalized = (sourcePriority >= 2) ? 1.0 : (sourcePriority == 1 ? 0.5 : 0.0);

        // Promotional penalty
        double promoPenalty = SearchValidation.IsPromotionalTitle(result.Title) ? 0.25 : 0.0;

        // Compute raw score with weighted components
        double rawScore =
            (titleContainment * W_TitleContainment) +
            (authorMatch * W_AuthorMatch) +
            (titleFuzzy * W_TitleFuzzy) +
            (completenessScore * W_Completeness) +
            (sourcePriorityNormalized * W_Source) +
            (asinMatch * W_AsinExact) +
            (seriesMatch * W_Series);

        // Apply promo penalty and clamp to 0..1
        rawScore = Math.Max(0.0, Math.Min(1.0, rawScore - promoPenalty));

        // Small extra boost if title fuzzy is very high but containment low
        if (titleContainment < 0.4 && titleFuzzy > 0.85)
        {
            rawScore = Math.Min(1.0, rawScore + (titleFuzzy * 0.1));
        }

        // Apply final boost for OpenLibrary sources
        if (isOpenLibrary)
        {
            rawScore = Math.Min(1.0, rawScore + 0.15);
        }

        // Determine drop reason (if any)
        string dropReason = string.Empty;

        // Filter logic is now handled by SearchResultFilterPipeline
        // Drop reasons here are for score-based filtering only

        return new ScoredSearchResult(
            result,
            rawScore,
            containmentScore,
            fuzzyScore,
            dropReason);
    }

    private double ComputeAuthorMatch(string? artist, string query)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(query))
                return 0.0;

            var queryTokens = TokenizeAndNormalize(query);
            if (!queryTokens.Any())
                return 0.0;

            var artistTokens = new HashSet<string>(TokenizeAndNormalize(artist), StringComparer.OrdinalIgnoreCase);
            int matchedAuthor = queryTokens.Count(qt => artistTokens.Contains(qt));
            return Math.Min(1.0, (double)matchedAuthor / queryTokens.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogDebug(ex, "Failed to compute author match");
            return 0.0;
        }
    }

    private double ComputeAsinMatch(string? asin, string query)
    {
        if (!string.IsNullOrWhiteSpace(asin) && !string.IsNullOrWhiteSpace(query) &&
            string.Equals(asin, query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }
        return 0.0;
    }

    private double ComputeSeriesMatch(string? series, string query)
    {
        if (string.IsNullOrWhiteSpace(series) || string.IsNullOrWhiteSpace(query))
            return 0.0;

        var seriesTokens = new HashSet<string>(TokenizeAndNormalize(series));
        var qtoks = TokenizeAndNormalize(query);

        if (!qtoks.Any())
            return 0.0;

        var matched = qtoks.Count(qt => seriesTokens.Contains(qt));
        return Math.Min(1.0, (double)matched / qtoks.Count);
    }

    private static List<string> TokenizeAndNormalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text.Split(new[] { ' ', '\t', '\n', '\r', '-', '_', '.', ',', ':', ';' },
                         StringSplitOptions.RemoveEmptyEntries)
                   .Select(t => t.Trim().ToLowerInvariant())
                   .Where(t => t.Length > 0)
                   .ToList();
    }
}

/// <summary>
/// A search result with computed relevance scores.
/// </summary>
public record ScoredSearchResult(
    SearchResult Result,
    double Score,
    double ContainmentScore,
    double FuzzyScore,
    string DropReason);

