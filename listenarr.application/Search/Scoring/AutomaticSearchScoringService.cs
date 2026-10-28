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
using Listenarr.Application.Search.Matching;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Scoring;

public sealed class AutomaticSearchScoringService(
    IQualityProfileService qualityService, IAudiobookRepository books,
    ILogger<AutomaticSearchScoringService> logger) : IAutomaticSearchScoringService
{
    private readonly BookIdentityMatcher _matcher = new();

    public async Task<AutomaticSearchAssessment> ScoreAsync(Audiobook book, List<SearchResult> results,
        QualityProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BookMatchContext context;
        try { context = BookMatchContext.From(book, profile); }
        catch (ArgumentException) { context = new([], [], null, "invalid_reference"); }
        var identities = new Dictionary<SearchResult, BookIdentityAssessment>(ReferenceEqualityComparer.Instance);
        var sources = new Dictionary<SearchResult, string>(ReferenceEqualityComparer.Instance);
        foreach (var result in results) sources.TryAdd(result, SearchCandidateFingerprint.Compute(result));
        foreach (var result in results)
            identities.TryAdd(result, context.Fingerprint == "invalid_reference"
                ? new(BookIdentityStatus.AssessmentUnavailable, "invalid_reference")
                : _matcher.Evaluate(context, BookIdentityMatcher.Snapshot(result), cancellationToken));
        // Every candidate is scored. The quality service sorts its output; associate by reference.
        var quality = await qualityService.ScoreSearchResults(results, profile);
        cancellationToken.ThrowIfCancellationRequested();
        var associatedQuality = new Dictionary<SearchResult, QualityScore?>(ReferenceEqualityComparer.Instance);
        foreach (var score in quality)
            if (!associatedQuality.TryAdd(score.SearchResult, score)) associatedQuality[score.SearchResult] = null;
        var scores = results.Select(result =>
        {
            if (!associatedQuality.TryGetValue(result, out var associated) || associated == null)
                return new AutomaticCandidateScore(new QualityScore
                {
                    SearchResult = result,
                    RejectionReasons = ["Quality result association unavailable"]
                }, new(BookIdentityStatus.AssessmentUnavailable, "unassociated_quality_result"), sources[result]);
            return new AutomaticCandidateScore(associated, identities[result], sources[result]);
        }).ToArray();
        foreach (var score in scores)
            logger.LogInformation("Automatic candidate {Id}: identity={Identity}, reason={Reason}, quality={Quality}, eligible={Eligible}",
                score.SearchResult.Id, score.Identity.Status, score.Identity.Reason, score.TotalScore, !score.IsRejected);
        return new(context.Fingerprint, scores);
    }

    public AutomaticCandidateScore? SelectBest(AutomaticSearchAssessment assessment) =>
        assessment.Candidates.Where(s => !s.IsRejected)
            .OrderByDescending(s => s.TotalScore)
            .ThenBy(s => s.SearchResult.IndexerId)
            .ThenBy(s => s.SearchResult.Id, StringComparer.Ordinal)
            .ThenBy(s => s.SearchResult.TorrentUrl, StringComparer.Ordinal)
            .ThenBy(s => s.SearchResult.NzbUrl, StringComparer.Ordinal).FirstOrDefault();

    public async Task<bool> IsCurrentAsync(AutomaticSearchAssessment assessment, int bookId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (assessment.Candidates.Any(c => !c.SourceIsCurrent)) return false;
        var current = await books.GetByIdSnapshotAsync(bookId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (current == null || current.QualityProfile == null || assessment.TargetFingerprint == "invalid_reference") return false;
        try { return BookMatchContext.From(current, current.QualityProfile).Fingerprint == assessment.TargetFingerprint; }
        catch (ArgumentException) { return false; }
    }
}
