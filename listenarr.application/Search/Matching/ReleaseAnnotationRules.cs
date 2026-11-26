namespace Listenarr.Application.Search.Matching;

/// <summary>Fixed, bounded rule families. Adding a spelling does not add another identity path.</summary>
internal static class ReleaseAnnotationRules
{
    public const int MaximumFieldLength = 256;

    public static ReleaseAnnotationResult Recognize(string value, ReadOnlySpan<char> tail,
        BookMatchContext context, bool scene, bool beforeTitle, bool technical, bool bracketed, bool inBracket, bool signed, bool delimitedPrefix,
        ReleaseAssessmentBudget budget)
    {
        var result = ReleaseTechnicalRule.Recognize(value, bracketed, inBracket, signed, context.AnnotationCatalog, technical);
        // A language code of at most three letters (the parser's table mixes codes and full names, so length is the only code test)
        // that is also a known credit or series name is that name, not a declared language. Full names such as German stay declared.
        if (result.RuleId == "technical.language" && value.Length <= 3 && (context.Authors.Concat(context.Narrators).Any(n => IdentityText.PersonEqual(n, value))
            || context.SeriesCatalog.Resolve(value).Count > 0))
            result = default;
        var narrator = ReleaseNarratorRule.Recognize(value, tail, context, scene, beforeTitle, technical, budget);
        var group = ReleaseGroupRule.Recognize(value, beforeTitle, bracketed, technical, delimitedPrefix, context.AnnotationCatalog);
        if (narrator.Outcome == AnnotationOutcome.Ambiguous) return narrator;
        if (group.Explained && (context.Authors.Concat(context.Narrators).Any(n => IdentityText.PersonEqual(n, value))
            || context.SeriesCatalog.Resolve(value).Count > 0))
            return new(AnnotationOutcome.Ambiguous, ReleaseSpanRole.Unknown, Reason: "annotation_role_ambiguous");
        var count = (result.Explained ? 1 : 0) + (narrator.Explained ? 1 : 0) + (group.Explained ? 1 : 0);
        if (count > 1) return new(AnnotationOutcome.Ambiguous, ReleaseSpanRole.Unknown, Reason: "annotation_role_ambiguous");
        return result.Explained ? result : narrator.Explained ? narrator : group;
    }
}
