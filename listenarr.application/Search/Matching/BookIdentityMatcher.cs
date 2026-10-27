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
namespace Listenarr.Application.Search.Matching;

public sealed class BookIdentityMatcher
{
    public BookIdentityAssessment Evaluate(BookMatchContext target, CandidateIdentityEvidence evidence)
    {
        try
        {
            if (evidence.Authors is null || evidence.Identifiers is null
                || evidence.Authors.Any(a => a is null)
                || evidence.Identifiers.Any(id => id is null || id.Namespace is null || id.Value is null))
                return new(BookIdentityStatus.AssessmentUnavailable, "invalid_identity_evidence");
            if (evidence.Authors.Count > 64 || evidence.Identifiers.Count > 64)
                return new(BookIdentityStatus.AssessmentUnavailable, "evidence_budget_exceeded");
            var supplied = evidence.Authors
                .Where(a => a.Origin is IdentityEvidenceOrigin.ProviderSupplied or IdentityEvidenceOrigin.Verified)
                .Select(a => IdentityText.Canonical(a.Name)).Where(IdentityText.IsContributorName).ToArray();
            var authorMatch = supplied.Any(a => target.Authors.Any(t => IdentityText.PersonEqual(t, a)));
            var authorConflict = supplied.Length > 0 && target.Authors.Count > 0 && !authorMatch;
            var identifierMatch = evidence.Identifiers.Any(id => id.Origin == IdentityEvidenceOrigin.Verified &&
                (string.Equals(id.Namespace, "asin", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(target.Asin) && string.Equals(id.Value.Trim(), target.Asin.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(id.Namespace, "isbn", StringComparison.OrdinalIgnoreCase) &&
                 target.Isbn.Any(value => NormalizeIsbn(value).Length > 0 && NormalizeIsbn(value) == NormalizeIsbn(id.Value))));
            var release = IdentityText.Canonical(evidence.ReleaseName);
            if (target.Titles.Count == 0)
                return identifierMatch && !authorConflict
                    ? new(BookIdentityStatus.ConfirmedIdentifier, "verified_identifier")
                    : new(BookIdentityStatus.MissingReference, "missing_reference");
            var match = ReleaseNameInterpreter.Match(release, target, supplied);
            if (authorConflict)
                return new(BookIdentityStatus.ConflictingEvidence, "author_conflict", match.Title);
            if (identifierMatch && !match.Complete)
                return release.Length == 0 ? new(BookIdentityStatus.ConfirmedIdentifier, "verified_identifier")
                    : new(BookIdentityStatus.ConflictingEvidence, "identifier_title_conflict");
            if (!match.Complete)
                return new(match.FoundTitle ? BookIdentityStatus.UnresolvedTitle : BookIdentityStatus.DifferentWork,
                    match.FoundTitle ? "unexplained_release_text" : "complete_title_not_found");
            if (identifierMatch) return new(BookIdentityStatus.ConfirmedIdentifier, "verified_identifier", match.Title);
            if (authorMatch || match.Credited)
                return new(BookIdentityStatus.CorroboratedTitle, "complete_title_and_credit", match.Title);
            return new(BookIdentityStatus.ExactTitleOnly, "exact_title_without_corroboration", match.Title);
        }
        catch (ArgumentException)
        {
            return new(BookIdentityStatus.AssessmentUnavailable, "invalid_identity_text");
        }
    }

    private static string NormalizeIsbn(string value) => value.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    public static CandidateIdentityEvidence Snapshot(SearchResult result) =>
        result.IdentityEvidence is { } facts
            ? facts with { Authors = facts.Authors?.ToArray()!, Identifiers = facts.Identifiers?.ToArray()! }
            : new() { ReleaseName = result.Title ?? string.Empty };
}
