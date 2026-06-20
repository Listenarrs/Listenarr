/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Infrastructure.Downloads.Submission;

public sealed class DirectDownloadSourceResolver(
    IIndexerRepository indexerRepository) : IDownloadSourceResolver
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
        if (indexer == null ||
            !indexer.IsEnabled ||
            !string.Equals(indexer.Implementation, "InternetArchive", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.EndsWith("archive.org", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
        {
            throw new DownloadClientSubmissionException(
                "The direct-download source is not trusted.");
        }

        return new PreparedDirectDownloadSubmission(
            candidate.Title,
            candidate.Artist,
            candidate.Album,
            candidate.Source,
            candidate.Quality,
            candidate.Language,
            candidate.Size,
            locator,
            uri);
    }
}
