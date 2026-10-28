/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Common;

namespace Listenarr.Application.Downloads.Submission;

public static class TrustedDownloadCandidateFactory
{
    public static TrustedDownloadCandidate Create(SearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var protocol = ResolveProtocol(result);
        var locators = new List<DownloadSourceLocator>();
        if (result.TorrentFileContent is { Length: > 0 })
        {
            locators.Add(new(DownloadSourceLocatorKind.TorrentBytes, Convert.ToBase64String(result.TorrentFileContent)));
        }
        if (!string.IsNullOrWhiteSpace(result.MagnetLink))
        {
            locators.Add(new(DownloadSourceLocatorKind.Magnet, result.MagnetLink));
        }
        if (protocol == DownloadProtocol.DirectDownload && result.DirectDownloadArtifacts.Count > 0)
        {
            locators.AddRange(result.DirectDownloadArtifacts.Select(artifact => new DownloadSourceLocator(
                DownloadSourceLocatorKind.DirectUrl,
                artifact.Url,
                artifact.FileName,
                artifact.ExpectedSize,
                artifact.Packaging)));
        }
        else if (!string.IsNullOrWhiteSpace(result.TorrentUrl))
        {
            locators.Add(new(
                protocol == DownloadProtocol.DirectDownload
                    ? DownloadSourceLocatorKind.DirectUrl
                    : DownloadSourceLocatorKind.TorrentUrl,
                result.TorrentUrl));
        }
        if (!string.IsNullOrWhiteSpace(result.NzbUrl))
        {
            locators.Add(new(DownloadSourceLocatorKind.NzbUrl, result.NzbUrl));
        }
        if (!string.IsNullOrWhiteSpace(result.Id))
        {
            locators.Add(new(DownloadSourceLocatorKind.ReleaseId, result.Id));
        }

        if (protocol == DownloadProtocol.Unknown || locators.Count == 0)
        {
            throw new DownloadClientSubmissionException(
                "The selected search result does not contain a trusted download source.");
        }

        return new TrustedDownloadCandidate(
            result.Id,
            result.Title,
            result.Artist,
            result.Album,
            result.Source,
            result.Quality,
            result.Language,
            result.Size,
            result.Seeders,
            new DownloadSourceDescriptor(
                result.IndexerId,
                result.IndexerImplementation,
                protocol,
                locators,
                result.TorrentFileName));
    }

    private static DownloadProtocol ResolveProtocol(SearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.NzbUrl) ||
            string.Equals(result.DownloadType, "Usenet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.DownloadType, "NZB", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadProtocol.Usenet;
        }

        if (result.TorrentFileContent is { Length: > 0 } ||
            !string.IsNullOrWhiteSpace(result.MagnetLink) ||
            (string.Equals(result.DownloadType, "Torrent", StringComparison.OrdinalIgnoreCase) &&
             !string.IsNullOrWhiteSpace(result.TorrentUrl)))
        {
            return DownloadProtocol.Torrent;
        }

        if (string.Equals(result.DownloadType, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.IndexerImplementation, "InternetArchive", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadProtocol.DirectDownload;
        }

        if (!string.IsNullOrWhiteSpace(result.TorrentUrl))
        {
            return DownloadProtocol.Torrent;
        }

        return DownloadProtocol.Unknown;
    }
}
