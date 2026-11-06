using System.Text.RegularExpressions;

namespace Listenarr.Application.Search.Matching;

internal static class EditionCompatibilityPolicy
{
    private static readonly Regex Markers = new(
        @"\b(?:unabridged|unabr|abridged|adapted|adaptation|dramatized|dramatised|retold|retelling|modernized|modernised|simplified|full[ -]cast)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex TitleAnnotation = new(
        @"[\[(](?<label>[^\[\]()]{1,256})[\])]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // Only separately supplied subtitles, title annotations, and interpreted annotation
    // spans establish edition evidence. A bare word inside a literal title does not.
    public static IEnumerable<string> FromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 16384) return [];
        return TitleAnnotation.Matches(title).SelectMany(m => Parse(m.Groups["label"].Value)).ToArray();
    }

    public static IEnumerable<string> Parse(string? value, bool technical = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384) return [];
        var canonical = IdentityText.Presentation(value.Trim().Trim('(', ')', '[', ']').Trim())
            .TrimEnd('.', ':').ToLowerInvariant();
        foreach (var article in new[] { "a ", "an ", "the " })
            if (canonical.StartsWith(article, StringComparison.Ordinal)) { canonical = canonical[article.Length..]; break; }
        var markers = Markers.Matches(canonical).Select(m => m.Value).ToArray();
        if (markers.Length == 0) return [];
        if (markers.All(m => m is "unabridged" or "unabr" or "abridged") && !ReleaseTechnicalRule.Match(canonical)) return [];
        var first = Markers.Match(canonical);
        if (!technical && first.Index > 0 && canonical[..first.Index] is not ("a " or "an " or "the ")) return [];
        // Abridgement labels are categorical. Adaptations retain their full designation;
        // a generic "adapted" label cannot prove "Adapted for Modern Readers".
        return markers.All(m => m is "unabridged" or "unabr" or "abridged")
            ? markers.Select(m => m == "unabr" ? "unabridged" : m).Distinct().ToArray()
            : [canonical.Replace("dramatised", "dramatized", StringComparison.Ordinal)
                .Replace("modernised", "modernized", StringComparison.Ordinal)];
    }
}
