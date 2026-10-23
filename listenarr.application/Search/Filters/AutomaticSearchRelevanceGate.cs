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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Filters;

/// <summary>
/// Relevance gate for the automatic-search paths only. Rejects indexer results that do not
/// name the audiobook being searched for — e.g. a Patterson Hood concert recording offered
/// for a James Patterson audiobook on the single shared "Patterson" surname, or a different
/// Dickens novel offered for David Copperfield on the author's name alone.
///
/// This is deliberately NOT an <see cref="ISearchResultFilter"/>: the
/// <see cref="SearchResultFilterPipeline"/> filters (product-like, promotional, …) are
/// tuned for metadata-page results and reject legitimate raw indexer releases such as
/// "The Lord of the Rings" (ring) or "Man's Search for Meaning" (search). Automatic
/// search runs this gate alone, before quality scoring; manual search is untouched.
///
/// A result is judged on its title AND its structured author field, so providers that
/// return title and author separately (MyAnonamouse: Title "Dune", Artist "Frank
/// Herbert") corroborate the same way as "Frank Herbert - Dune [M4B]".
///
/// The title match is judged on its own, never diluted by the length of a stored subtitle
/// or by the number of credited authors: a release that names the main title ("Good
/// Omens [M4B]") passes even when the stored title carries a long subtitle, and a
/// single-word title ("Stories") is corroborated by any one of several credited authors.
/// </summary>
public class AutomaticSearchRelevanceGate
{
    /// <summary>
    /// Fraction of the audiobook's significant title tokens — the main title (before a
    /// subtitle separator) or the full title, whichever matches better — that must appear
    /// in the result's title or artist field.
    /// </summary>
    public const double MinTitleRelevance = 0.5;

    /// <summary>
    /// Fraction of one credited author's significant tokens that must appear in the result
    /// for that author to corroborate a generic title ("Dickens" alone vouches for
    /// "Charles Dickens").
    /// </summary>
    public const double MinAuthorRelevance = 0.5;

    /// <summary>
    /// Maximum number of significant title tokens for a title to count as "generic" and
    /// therefore require author corroboration. Default 1: only single-significant-word
    /// titles ("Betrayed", "Vision", "Stories") are too ambiguous to match on the title
    /// word alone.
    /// </summary>
    public const int MaxGenericTitleTokens = 1;

    public const string TitleRejectionReason = "title_not_relevant";
    public const string AuthorRejectionReason = "author_not_corroborated";

    private static readonly string[] SubtitleSeparators = { ":", ";", " - ", " – ", " — ", "(", "[" };

    private readonly ILogger<AutomaticSearchRelevanceGate> _logger;

    public AutomaticSearchRelevanceGate(ILogger<AutomaticSearchRelevanceGate> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the results that are relevant to <paramref name="audiobook"/>, logging each
    /// rejection. The caller decides what an empty list means.
    /// </summary>
    public List<SearchResult> Apply(List<SearchResult> results, Audiobook audiobook)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(audiobook);

        var kept = new List<SearchResult>(results.Count);
        foreach (var result in results)
        {
            var verdict = Evaluate(result.Title, result.Artist, audiobook.Title, audiobook.Authors);
            if (verdict.IsRelevant)
            {
                kept.Add(result);
                continue;
            }

            _logger.LogInformation(
                "Rejected automatic-search result '{ResultTitle}' (artist '{ResultArtist}') for audiobook '{Title}': {Reason} (main title {MainTitle:P0}, full title {FullTitle:P0}, author corroborated: {AuthorCorroborated})",
                result.Title, result.Artist, audiobook.Title, verdict.Reason,
                verdict.MainTitleRelevance, verdict.FullTitleRelevance, verdict.AuthorCorroborated);
        }

        if (kept.Count < results.Count)
        {
            _logger.LogInformation("Rejected {Removed} of {Total} results as irrelevant for audiobook '{Title}'",
                results.Count - kept.Count, results.Count, audiobook.Title);
        }

        return kept;
    }

    public bool IsRelevant(SearchResult result, Audiobook audiobook)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(audiobook);

        return Evaluate(result.Title, result.Artist, audiobook.Title, audiobook.Authors).IsRelevant;
    }

    /// <summary>
    /// Judges a result against the audiobook. The title is matched on its own: the main
    /// title (the part before a subtitle separator) or the full title must reach
    /// <see cref="MinTitleRelevance"/>. A generic title (see <see cref="MaxGenericTitleTokens"/>)
    /// additionally needs one credited author to appear in the result; a title that is
    /// distinctive on its own does not. Author tokens never substitute for the title, so a
    /// different book by the same author is rejected.
    ///
    /// Fails open (relevant) when the audiobook has no significant title tokens, and a
    /// generic title fails open when no author is known to corroborate with.
    /// </summary>
    public static RelevanceVerdict Evaluate(
        string? resultTitle,
        string? resultArtist,
        string? audiobookTitle,
        IEnumerable<string>? authors)
    {
        var resultTokens = ResultTokens(resultTitle, resultArtist);
        var fullTitle = Tokens(audiobookTitle);
        if (fullTitle.Count == 0)
        {
            return new RelevanceVerdict(true, null, 1.0, 1.0, AuthorCorroborated: true);
        }

        var mainTitle = Tokens(MainTitle(audiobookTitle));
        if (mainTitle.Count == 0) mainTitle = fullTitle;

        var mainRelevance = Relevance(mainTitle, resultTokens);
        var fullRelevance = Relevance(fullTitle, resultTokens);
        var (authorKnown, authorCorroborated) = Corroboration(resultTokens, authors);

        var mainMatches = TitleMatches(mainTitle, mainRelevance, authorKnown, authorCorroborated);
        var fullMatches = TitleMatches(fullTitle, fullRelevance, authorKnown, authorCorroborated);
        if (mainMatches || fullMatches)
        {
            return new RelevanceVerdict(true, null, mainRelevance, fullRelevance, authorCorroborated);
        }

        var reason = Math.Max(mainRelevance, fullRelevance) >= MinTitleRelevance
            ? AuthorRejectionReason
            : TitleRejectionReason;
        return new RelevanceVerdict(false, reason, mainRelevance, fullRelevance, authorCorroborated);
    }

    /// <summary>
    /// Fraction of the audiobook's significant title tokens found in the result (title or
    /// artist): the main title or the full title, whichever matches better. 1.0 when the
    /// audiobook has no significant title tokens.
    /// </summary>
    public static double ComputeTitleRelevance(string? resultTitle, string? resultArtist, string? audiobookTitle)
    {
        var verdict = Evaluate(resultTitle, resultArtist, audiobookTitle, authors: null);
        return Math.Max(verdict.MainTitleRelevance, verdict.FullTitleRelevance);
    }

    /// <summary>
    /// True when at least one credited author is named in the result: a fraction of that
    /// author's significant tokens of at least <see cref="MinAuthorRelevance"/> appears in the
    /// result title or artist field. False when no author is known.
    /// </summary>
    public static bool IsAuthorCorroborated(string? resultTitle, string? resultArtist, IEnumerable<string>? authors) =>
        Corroboration(ResultTokens(resultTitle, resultArtist), authors).Corroborated;

    /// <summary>
    /// The part of a stored title before its subtitle: "Mistborn: The Final Empire" → "Mistborn",
    /// "Good Omens: The Nice and Accurate Prophecies of Agnes Nutter, Witch" → "Good Omens".
    /// Returns the whole title when it has no subtitle separator.
    /// </summary>
    public static string MainTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var cut = title.Length;
        foreach (var separator in SubtitleSeparators)
        {
            var index = title.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0 && index < cut) cut = index;
        }

        return title[..cut].Trim();
    }

    private static bool TitleMatches(HashSet<string> titleTokens, double relevance, bool authorKnown, bool authorCorroborated)
    {
        if (relevance < MinTitleRelevance) return false;
        // Distinctive multi-word titles stand on their own; a generic one needs an author to
        // vouch for it — unless no author is known, in which case we cannot judge.
        if (titleTokens.Count > MaxGenericTitleTokens) return true;
        return authorCorroborated || !authorKnown;
    }

    private static double Relevance(HashSet<string> expected, HashSet<string> resultTokens)
    {
        if (expected.Count == 0) return 1.0;
        return (double)expected.Count(resultTokens.Contains) / expected.Count;
    }

    private static (bool Known, bool Corroborated) Corroboration(HashSet<string> resultTokens, IEnumerable<string>? authors)
    {
        var known = false;
        if (authors == null) return (false, false);
        foreach (var author in authors)
        {
            var authorTokens = Tokens(author);
            if (authorTokens.Count == 0) continue;
            known = true;
            if (Relevance(authorTokens, resultTokens) >= MinAuthorRelevance) return (true, true);
        }

        return (known, false);
    }

    private static HashSet<string> ResultTokens(string? resultTitle, string? resultArtist)
    {
        var tokens = Tokens(resultTitle);
        foreach (var t in SignificantTokens.From(resultArtist)) tokens.Add(t);
        return tokens;
    }

    private static HashSet<string> Tokens(string? text) =>
        new(SignificantTokens.From(text), StringComparer.Ordinal);
}

/// <summary>
/// Outcome of <see cref="AutomaticSearchRelevanceGate.Evaluate"/>. <paramref name="Reason"/>
/// is null when the result is relevant.
/// </summary>
public sealed record RelevanceVerdict(
    bool IsRelevant,
    string? Reason,
    double MainTitleRelevance,
    double FullTitleRelevance,
    bool AuthorCorroborated);
