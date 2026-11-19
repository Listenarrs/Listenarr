namespace Listenarr.Application.Search.Matching;

internal enum AnnotationOutcome { NoMatch, Explained, UnknownReference, Conflict, Ambiguous }

/// <summary>A rule explains a field; only the identity coordinator can confirm a work.</summary>
internal readonly record struct ReleaseAnnotationResult(
    AnnotationOutcome Outcome, ReleaseSpanRole Role, string? RuleId = null,
    string? Reference = null, string? Strength = null, string? Reason = null,
    IReadOnlyList<ReleaseSeriesClaim>? PositionClaims = null)
{
    public bool Explained => Outcome == AnnotationOutcome.Explained;
    public ReleaseSpan Span(ReleaseTextView view, int start, int length) => view.Span(start, length, Role) with
    { RuleId = RuleId, Reference = Reference, MatchStrength = Strength };
}

internal sealed record ReleaseSeriesClaim(string Name, decimal Position)
{
    public static bool Compatible(IEnumerable<ReleaseSeriesClaim> claims) =>
        claims.GroupBy(c => c.Name, StringComparer.Ordinal).All(g => g.Select(c => c.Position).Distinct().Count() == 1);

    public static string Signature(IEnumerable<ReleaseSeriesClaim> claims) => string.Join("|", claims
        .Distinct().OrderBy(c => c.Name, StringComparer.Ordinal).Select(c =>
            c.Name.Length + ":" + c.Name + "=" + c.Position.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
