/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Downloads.Contracts;

public interface IDownloadReferenceService
{
    string Create(TrustedDownloadCandidate candidate);
    TrustedDownloadCandidate Read(string downloadReference);
}

public interface IDownloadReferenceProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

public interface IDownloadSubmissionPreparer
{
    Task<PreparedDownloadSubmission> PrepareAsync(
        TrustedDownloadCandidate candidate,
        string? provisionalDownloadId = null,
        CancellationToken cancellationToken = default);
}

public interface IDownloadSourceResolver
{
    int Priority { get; }
    bool CanResolve(TrustedDownloadCandidate candidate);
    Task<PreparedDownloadSubmission> ResolveAsync(
        TrustedDownloadCandidate candidate,
        string? provisionalDownloadId,
        CancellationToken cancellationToken);
}

public interface INzbFileDownloader
{
    Task<byte[]> DownloadAsync(string url, int? indexerId, CancellationToken cancellationToken = default);
}

public interface ITorrentMetadataService
{
    PreparedTorrentSubmission Prepare(
        TrustedDownloadCandidate candidate,
        byte[]? torrentBytes,
        string? magnetUri,
        string originalLocator,
        string? fileName = null);
}
