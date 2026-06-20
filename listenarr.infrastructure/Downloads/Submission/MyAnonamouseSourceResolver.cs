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

public sealed class MyAnonamouseSourceResolver(
    MyAnonamouseTorrentPreparationService preparationService,
    ITorrentMetadataService metadataService) : IDownloadSourceResolver
{
    public int Priority => 100;

    public bool CanResolve(TrustedDownloadCandidate candidate)
        => candidate.SourceDescriptor.Protocol == DownloadProtocol.Torrent &&
           string.Equals(
               candidate.SourceDescriptor.IndexerImplementation,
               "MyAnonamouse",
               StringComparison.OrdinalIgnoreCase);

    public async Task<PreparedDownloadSubmission> ResolveAsync(
        TrustedDownloadCandidate candidate,
        string? provisionalDownloadId,
        CancellationToken cancellationToken)
    {
        var torrentUrl = candidate.SourceDescriptor.Locators
            .FirstOrDefault(locator => locator.Kind == DownloadSourceLocatorKind.TorrentUrl)?.Value;
        if (string.IsNullOrWhiteSpace(torrentUrl))
        {
            throw new DownloadClientSubmissionException(
                "MyAnonamouse did not provide a torrent download locator.");
        }

        var result = new SearchResult
        {
            Id = candidate.Id,
            Title = candidate.Title,
            Artist = candidate.Artist,
            Album = candidate.Album,
            Source = candidate.Source,
            Quality = candidate.Quality,
            Language = candidate.Language,
            Size = candidate.Size,
            Seeders = candidate.Seeders,
            IndexerId = candidate.SourceDescriptor.IndexerId,
            IndexerImplementation = candidate.SourceDescriptor.IndexerImplementation,
            TorrentUrl = torrentUrl,
            DownloadType = "Torrent"
        };
        await preparationService.PrepareAsync(result, provisionalDownloadId, cancellationToken);
        if (result.TorrentFileContent is not { Length: > 0 })
        {
            throw new DownloadClientSubmissionException(
                "MyAnonamouse torrent metadata could not be prepared.");
        }

        return metadataService.Prepare(
            candidate,
            result.TorrentFileContent,
            null,
            torrentUrl,
            result.TorrentFileName);
    }
}
