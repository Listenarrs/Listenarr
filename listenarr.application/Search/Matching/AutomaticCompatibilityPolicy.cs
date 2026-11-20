using System.Globalization;

namespace Listenarr.Application.Search.Matching;

internal static class AutomaticCompatibilityPolicy
{
    private static readonly IReadOnlyDictionary<string, string> LanguageNames = BuildLanguages();
    private static readonly HashSet<string> MissingLanguages = new(
        ["unknown", "unspecified", "und", "mul", "any", "all"], StringComparer.OrdinalIgnoreCase);

    public static CandidateCompatibilityAssessment Evaluate(BookMatchContext target, SearchResult candidate, BookIdentityAssessment identity)
    {
        if (candidate.Language?.Length > 256 || candidate.Subtitle?.Length > 16384
            || target.PreferredLanguages.Any(l => l?.Length > 256))
            return new(["compatibility_evidence_budget_exceeded"]);
        var reasons = new List<string>();
        var languages = identity.Languages.ToList();
        if (candidate.LanguageOrigin is not (IdentityEvidenceOrigin.ReleaseNameDerived or IdentityEvidenceOrigin.ContextDerived))
            languages.AddRange(LanguageValues(candidate.Language).Select(supplied => new LanguageEvidence(supplied, candidate.LanguageOrigin, "result.language")));
        var normalized = languages.Select(l => NormalizeLanguage(l.Language)).Where(l => l != null).Distinct().ToArray();
        if (normalized.Length > 1) reasons.Add("language_evidence_conflict");
        var reference = LanguageValues(target.Language).ToArray();
        if (reference.Length > 1) reasons.Add("reference_language_ambiguous");
        var preferred = target.PreferredLanguages.SelectMany(LanguageValues).ToArray();
        if (normalized.Any(l => reference.Length > 0 && !reference.Contains(l) || preferred.Length > 0 && !preferred.Contains(l)))
            reasons.Add("language_mismatch");

        var editions = identity.Editions.Concat(EditionCompatibilityPolicy.Parse(candidate.Subtitle)
            .Select(e => new EditionEvidence(e, IdentityEvidenceOrigin.Unattributed))).Distinct().ToArray();
        var labels = editions.Select(e => e.Label).Distinct().ToArray();
        if (labels.Contains("abridged") && labels.Contains("unabridged")) reasons.Add("edition_evidence_conflict");
        if (target.Editions.Contains("abridged") && target.Editions.Contains("unabridged")) reasons.Add("reference_edition_conflict");
        if (target.Editions.Count > 0)
        {
            if (labels.Any(e => e != "unabridged" && !target.Editions.Contains(e))
                || labels.Contains("unabridged") && target.Editions.Contains("abridged")) reasons.Add("edition_mismatch");
            if (identity.Status != BookIdentityStatus.ConfirmedIdentifier && target.Editions.Any(e => !labels.Contains(e)))
                reasons.Add("edition_evidence_required");
        }
        else if (identity.Status != BookIdentityStatus.ConfirmedIdentifier && labels.Any(e => e != "unabridged"))
            reasons.Add("edition_target_unresolved");
        return new(Array.AsReadOnly(reasons.Distinct().ToArray()))
        {
            Languages = Array.AsReadOnly(languages.ToArray()),
            Editions = Array.AsReadOnly(editions)
        };
    }

    private static IEnumerable<string> LanguageValues(string? value) => string.IsNullOrWhiteSpace(value) ? [] :
        value.Split([';', ',', '/', '|', '+'], StringSplitOptions.RemoveEmptyEntries).Select(NormalizeLanguage)
            .Where(l => l != null).Select(l => l!).Distinct(StringComparer.Ordinal);

    private static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var token = value.Trim();
        if (MissingLanguages.Contains(token)) return null;
        var region = token.IndexOfAny(['-', '_']);
        var primary = region is 2 or 3 ? token[..region] : token;
        var name = SearchResultAttributeParser.ParseLanguageFromCode(token)
            ?? SearchResultAttributeParser.ParseLanguageFromCode(primary) ?? token;
        if (LanguageNames.TryGetValue(name, out var code)) return code;
        if (region is 2 or 3 && LanguageNames.TryGetValue(token[..region], out code)) return code;
        return name.ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string> BuildLanguages()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures).Where(c => c.Name.Length > 0)
            .OrderBy(c => c.Name, StringComparer.Ordinal))
            foreach (var alias in new[] { culture.Name, culture.EnglishName, culture.NativeName, culture.ThreeLetterISOLanguageName })
                names.TryAdd(alias, culture.TwoLetterISOLanguageName.ToLowerInvariant());
        return names;
    }
}
