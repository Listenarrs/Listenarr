/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Infrastructure.Torrents;

namespace Listenarr.Infrastructure.Downloads.Submission;

public sealed class GenericTorrentSourceResolver(
    ITorrentFileDownloader torrentFileDownloader,
    ITorrentMetadataService metadataService) : IDownloadSourceResolver
{
    public int Priority => 0;

    public bool CanResolve(TrustedDownloadCandidate candidate)
        => candidate.SourceDescriptor.Protocol == DownloadProtocol.Torrent;

    public async Task<PreparedDownloadSubmission> ResolveAsync(
        TrustedDownloadCandidate candidate,
        string? provisionalDownloadId,
        CancellationToken cancellationToken)
    {
        var bytesLocator = Find(candidate, DownloadSourceLocatorKind.TorrentBytes);
        var magnet = Find(candidate, DownloadSourceLocatorKind.Magnet);
        var torrentUrl = Find(candidate, DownloadSourceLocatorKind.TorrentUrl);
        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(bytesLocator))
        {
            try
            {
                bytes = Convert.FromBase64String(bytesLocator);
            }
            catch (FormatException exception)
            {
                throw new DownloadClientSubmissionException(
                    "The cached torrent metadata is invalid.",
                    exception);
            }
        }

        if (bytes == null && string.IsNullOrWhiteSpace(magnet) && !string.IsNullOrWhiteSpace(torrentUrl))
        {
            var downloaded = await torrentFileDownloader.DownloadAsync(torrentUrl, cancellationToken);
            bytes = downloaded.TorrentBytes;
            magnet = downloaded.HasMagnet ? downloaded.MagnetUri : magnet;
            if (downloaded.IsEmpty)
            {
                throw new DownloadClientSubmissionException(
                    downloaded.FailureReason ?? "Torrent metadata could not be downloaded.");
            }
        }

        var original = torrentUrl ?? magnet ?? candidate.Id;
        return metadataService.Prepare(
            candidate,
            bytes,
            magnet,
            original,
            candidate.SourceDescriptor.FileName);
    }

    private static string? Find(
        TrustedDownloadCandidate candidate,
        DownloadSourceLocatorKind kind)
        => candidate.SourceDescriptor.Locators.FirstOrDefault(locator => locator.Kind == kind)?.Value;
}
