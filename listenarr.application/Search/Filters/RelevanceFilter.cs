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
namespace Listenarr.Application.Search.Filters;

/// <summary>
/// Context-aware filter that rejects search results whose title shares too few
/// significant tokens with the audiobook being searched for. Defends against
/// indexers returning matter that shares one or two tokens with the query —
/// e.g., a Patterson Hood concert recording being suggested for a James
/// Patterson audiobook on the single shared "Patterson" surname.
///
/// Without an audiobook context (manual search, AsinEnricher per-result calls)
/// the filter fails open — manual users keep seeing every result.
/// </summary>
public class RelevanceFilter : ISearchResultFilter
{
    /// <summary>
    /// Default fraction of significant audiobook tokens that must appear in the
    /// result title for the result to be considered relevant. Empirically chosen
    /// so that single-shared-token false matches (one author surname only) are
    /// rejected while multi-token legitimate matches pass.
    /// </summary>
    public const double DefaultMinRelevance = 0.30;

    /// <summary>
    /// Maximum number of significant title tokens for a title to count as "generic" and
    /// therefore require author corroboration (see <see cref="RequiresAuthorCorroboration"/>).
    /// Default 1: only single-significant-word titles ("Betrayed", "Vision", "Titans") are
    /// considered too ambiguous to match on the title word alone. Raise to be stricter.
    /// </summary>
    public const int MaxGenericTitleTokens = 1;

    public string FilterReason => "title_not_relevant";

    public bool ShouldFilter(SearchResult result, Audiobook? audiobook = null)
    {
        // No audiobook context (manual search, per-result AsinEnricher calls) — fail
        // open so those callers keep every result.
        if (audiobook == null) return false;

        var relevance = ComputeRelevance(result.Title, audiobook.Title, audiobook.Authors);
        if (relevance < DefaultMinRelevance) return true;

        // The combined title+author ratio alone clears a common single-word title on the
        // title word by itself: a 1-token title with a 2-token author gives an expected set
        // of {title, a1, a2}, so matching only the title word scores 1/3 = 0.33 > 0.30 even
        // though the result is a different book whose title merely *contains* that common word
        // (e.g. "Mark Johnson - Wasted: ...An Innocence Betrayed..." matched the wanted
        // "Betrayed" by Lindsay Buroker). For such generic titles, require the author to
        // corroborate the match.
        if (RequiresAuthorCorroboration(result.Title, audiobook.Title, audiobook.Authors)) return true;

        return false;
    }

    /// <summary>
    /// Computes a relevance ratio in [0, 1]: the fraction of distinct
    /// significant tokens from the audiobook's title + authors that appear
    /// in the result title.
    ///
    /// Returns 1.0 when the audiobook side has no significant tokens (cannot
    /// be judged — fail open rather than reject every result).
    /// </summary>
    public static double ComputeRelevance(
        string? resultTitle,
        string? audiobookTitle,
        IEnumerable<string>? authors)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in SignificantTokens.From(audiobookTitle)) expected.Add(t);
        if (authors != null)
        {
            foreach (var author in authors)
            {
                foreach (var t in SignificantTokens.From(author)) expected.Add(t);
            }
        }
        if (expected.Count == 0) return 1.0;

        var resultTokens = new HashSet<string>(SignificantTokens.From(resultTitle), StringComparer.OrdinalIgnoreCase);
        var hits = expected.Count(t => resultTokens.Contains(t));
        return (double)hits / expected.Count;
    }

    /// <summary>
    /// True when a result must be rejected for lacking author corroboration: the audiobook's
    /// title is generic (at most <see cref="MaxGenericTitleTokens"/> significant tokens), the
    /// author is known, and the result title shares none of the author's significant tokens.
    /// That is the signature of a common-word title collision rather than a real match.
    ///
    /// Fails open (returns false) when the title is distinctive enough on its own (more than
    /// <see cref="MaxGenericTitleTokens"/> significant tokens) or when no author is known to
    /// corroborate with. Note: this only gates the automatic-search path — manual search passes
    /// no audiobook context, so a legitimate author-less release of a single-word-titled book
    /// can still be grabbed by hand.
    /// </summary>
    public static bool RequiresAuthorCorroboration(
        string? resultTitle,
        string? audiobookTitle,
        IEnumerable<string>? authors)
    {
        var titleTokenCount = SignificantTokens.From(audiobookTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        // Distinctive multi-word titles can stand on their own — only generic short titles
        // need the author to vouch for the match.
        if (titleTokenCount == 0 || titleTokenCount > MaxGenericTitleTokens) return false;

        var authorTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (authors != null)
        {
            foreach (var author in authors)
            {
                foreach (var t in SignificantTokens.From(author)) authorTokens.Add(t);
            }
        }
        // No author to corroborate with — cannot judge, do not reject.
        if (authorTokens.Count == 0) return false;

        var resultTokens = new HashSet<string>(SignificantTokens.From(resultTitle), StringComparer.OrdinalIgnoreCase);
        var authorHits = authorTokens.Count(t => resultTokens.Contains(t));
        return authorHits == 0;
    }
}
