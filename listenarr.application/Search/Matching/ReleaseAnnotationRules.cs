namespace Listenarr.Application.Search.Matching;

/// <summary>Fixed, bounded rule families. Adding a spelling does not add another identity path.</summary>
internal static class ReleaseAnnotationRules
{
    public const int MaximumFieldLength = 256;

    public static ReleaseAnnotationResult Recognize(string value, ReadOnlySpan<char> tail,
        BookMatchContext context, bool scene, bool beforeTitle, bool technical, bool bracketed, bool signed,
        ReleaseAssessmentBudget budget)
    {
        var result = ReleaseTechnicalRule.Recognize(value, bracketed, signed);
        var narrator = ReleaseNarratorRule.Recognize(value, tail, context, scene, beforeTitle, technical, budget);
        var group = ReleaseGroupRule.Recognize(value, beforeTitle);
        var count = (result.Explained ? 1 : 0) + (narrator.Explained ? 1 : 0) + (group.Explained ? 1 : 0);
        if (count > 1) return new(AnnotationOutcome.Ambiguous, ReleaseSpanRole.Unknown, Reason: "annotation_role_ambiguous");
        return result.Explained ? result : narrator.Explained ? narrator : group;
    }
}
