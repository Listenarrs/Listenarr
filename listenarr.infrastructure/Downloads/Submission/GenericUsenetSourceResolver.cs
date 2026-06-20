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

public sealed class GenericUsenetSourceResolver(
    INzbFileDownloader downloader) : IDownloadSourceResolver
{
    public int Priority => 0;

    public bool CanResolve(TrustedDownloadCandidate candidate)
        => candidate.SourceDescriptor.Protocol == DownloadProtocol.Usenet;

    public async Task<PreparedDownloadSubmission> ResolveAsync(
        TrustedDownloadCandidate candidate,
        string? provisionalDownloadId,
        CancellationToken cancellationToken)
    {
        var url = candidate.SourceDescriptor.Locators
            .FirstOrDefault(locator => locator.Kind == DownloadSourceLocatorKind.NzbUrl)?.Value;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new DownloadClientSubmissionException("No NZB download locator was provided.");
        }

        var bytes = await downloader.DownloadAsync(
            url,
            candidate.SourceDescriptor.IndexerId,
            cancellationToken);
        return new PreparedUsenetSubmission(
            candidate.Title,
            candidate.Artist,
            candidate.Album,
            candidate.Source,
            candidate.Quality,
            candidate.Language,
            candidate.Size,
            url,
            bytes,
            candidate.SourceDescriptor.FileName ?? $"{SanitizeFileName(candidate.Title)}.nzb");
    }

    private static string SanitizeFileName(string value)
        => string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
