namespace Listenarr.Domain.Search;

public sealed record LanguageEvidence(string Language, IdentityEvidenceOrigin Origin, string Source);
public sealed record EditionEvidence(string Label, IdentityEvidenceOrigin Origin);

/// <summary>Work identity and edition/language eligibility are separate decisions.</summary>
public sealed record CandidateCompatibilityAssessment(IReadOnlyList<string> Reasons)
{
    public static CandidateCompatibilityAssessment Compatible { get; } = new([]);
    public bool IsCompatible => Reasons.Count == 0;
    public IReadOnlyList<LanguageEvidence> Languages { get; init; } = [];
    public IReadOnlyList<EditionEvidence> Editions { get; init; } = [];
}
