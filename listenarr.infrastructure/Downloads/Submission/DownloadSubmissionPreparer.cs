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

public sealed class DownloadSubmissionPreparer(
    IEnumerable<IDownloadSourceResolver> resolvers) : IDownloadSubmissionPreparer
{
    public Task<PreparedDownloadSubmission> PrepareAsync(
        TrustedDownloadCandidate candidate,
        string? provisionalDownloadId = null,
        CancellationToken cancellationToken = default)
    {
        var matches = resolvers
            .Where(resolver => resolver.CanResolve(candidate))
            .OrderByDescending(resolver => resolver.Priority)
            .ToList();
        if (matches.Count == 0)
        {
            throw new DownloadClientSubmissionException(
                "No source resolver supports the selected download.");
        }

        if (matches.Count > 1 && matches[0].Priority == matches[1].Priority)
        {
            throw new DownloadClientSubmissionException(
                "The selected download matched multiple source resolvers.");
        }

        return matches[0].ResolveAsync(candidate, provisionalDownloadId, cancellationToken);
    }
}
