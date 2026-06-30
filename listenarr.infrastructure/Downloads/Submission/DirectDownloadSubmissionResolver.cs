// Listenarr - Audiobook Management System
// Copyright (C) 2024-2026 Listenarr Contributors

namespace Listenarr.Infrastructure.Downloads.Submission;

internal sealed class DirectDownloadSubmissionResolver(
    IIndexerRepository indexerRepository,
    IEnumerable<IDirectDownloadSourcePolicy> sourcePolicies) : IDownloadSourceResolver
{
    public int Priority => 0;

    public bool CanResolve(TrustedDownloadCandidate candidate)
        => candidate.SourceDescriptor.Protocol == DownloadProtocol.DirectDownload;

    public async Task<PreparedDownloadSubmission> ResolveAsync(
        TrustedDownloadCandidate candidate,
        string? provisionalDownloadId,
        CancellationToken cancellationToken)
    {
        var locator = candidate.SourceDescriptor.Locators
            .FirstOrDefault(value => value.Kind == DownloadSourceLocatorKind.DirectUrl)?.Value;
        if (string.IsNullOrWhiteSpace(locator) ||
            !Uri.TryCreate(locator, UriKind.Absolute, out var uri) ||
            !OutboundRequestSecurity.TryValidateExternalHttpUri(uri, out _, allowPrivateTargets: true))
        {
            throw new DownloadClientSubmissionException("The direct-download URL is invalid.");
        }

        if (candidate.SourceDescriptor.IndexerId is not int indexerId)
        {
            throw new DownloadClientSubmissionException(
                "The direct-download source is not associated with a configured indexer.");
        }

        var indexer = await indexerRepository.GetByIdAsync(indexerId, cancellationToken);
        if (indexer == null || !indexer.IsEnabled)
        {
            throw new DownloadClientSubmissionException(
                "The direct-download source is not trusted.");
        }

        var policy = sourcePolicies
            .OrderBy(policy => policy.Priority)
            .FirstOrDefault(policy => policy.CanPrepare(indexer, candidate, uri));
        if (policy == null)
        {
            throw new DownloadClientSubmissionException(
                "The direct-download source is not trusted.");
        }

        // Store the policy selected at submission time. The DDL worker re-resolves
        // the same policy before fetching so adding a new source only requires a
        // new allow-list policy, not changes to the transfer processor.
        return new PreparedDirectDownloadSubmission(
            candidate.Title,
            candidate.Artist,
            candidate.Album,
            candidate.Source,
            candidate.Quality,
            candidate.Language,
            candidate.Size,
            locator,
            uri,
            policy.Key);
    }
}
