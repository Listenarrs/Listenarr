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
namespace Listenarr.Domain.Search;

public enum IdentityEvidenceOrigin { Unattributed, ProviderSupplied, ReleaseNameDerived, Verified }

public sealed record ContributorEvidence(string Name, IdentityEvidenceOrigin Origin);
public sealed record IdentifierEvidence(string Namespace, string Value, IdentityEvidenceOrigin Origin);

/// <summary>Candidate facts, not values inferred from the requested book or quality scorer.</summary>
public sealed record CandidateIdentityEvidence
{
    public string ReleaseName { get; init; } = string.Empty;
    public IReadOnlyList<ContributorEvidence> Authors { get; init; } = [];
    public IReadOnlyList<IdentifierEvidence> Identifiers { get; init; } = [];
}

public enum BookIdentityStatus
{
    ConfirmedIdentifier, CorroboratedTitle, ExactTitleOnly, UnresolvedTitle,
    ConflictingEvidence, DifferentWork, MissingReference, AssessmentUnavailable
}

public sealed record BookIdentityAssessment(
    BookIdentityStatus Status, string Reason, string? MatchedTitle = null)
{
    public bool IsConfirmed => Status is BookIdentityStatus.ConfirmedIdentifier or BookIdentityStatus.CorroboratedTitle;
}
