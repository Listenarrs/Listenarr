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

public sealed record AutomaticCandidateScore(QualityScore Quality, BookIdentityAssessment Identity, string SourceFingerprint)
{
    public SearchResult SearchResult => Quality.SearchResult;
    public int TotalScore => Quality.TotalScore;
    public bool SourceIsCurrent => SearchCandidateFingerprint.Compute(SearchResult) == SourceFingerprint;
    public bool IsRejected => !Identity.IsConfirmed || Quality.IsRejected || TotalScore <= 0 || !SourceIsCurrent;
    public IReadOnlyList<string> RejectionReasons =>
        Quality.RejectionReasons.Concat(!Identity.IsConfirmed ? [Identity.Reason] : Array.Empty<string>())
            .Concat(!SourceIsCurrent ? ["candidate_changed"] : Array.Empty<string>())
            .Concat(TotalScore <= 0 ? ["quality_score_not_positive"] : Array.Empty<string>()).ToArray();
}

public sealed record AutomaticSearchAssessment(
    string TargetFingerprint, IReadOnlyList<AutomaticCandidateScore> Candidates)
{
    public const string PolicyVersion = "CorroboratedAutomaticV1";
}
