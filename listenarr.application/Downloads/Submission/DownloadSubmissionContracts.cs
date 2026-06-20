/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Application.Downloads.Submission;

public enum DownloadSourceLocatorKind
{
    TorrentBytes,
    Magnet,
    TorrentUrl,
    NzbUrl,
    DirectUrl,
    ReleaseId
}

public sealed record DownloadSourceLocator(DownloadSourceLocatorKind Kind, string Value);

public sealed record DownloadSourceDescriptor(
    int? IndexerId,
    string? IndexerImplementation,
    DownloadProtocol Protocol,
    IReadOnlyList<DownloadSourceLocator> Locators,
    string? FileName = null);

public sealed record TrustedDownloadCandidate(
    string Id,
    string Title,
    string Artist,
    string Album,
    string Source,
    string? Quality,
    string? Language,
    long Size,
    int? Seeders,
    DownloadSourceDescriptor SourceDescriptor);

public abstract record PreparedDownloadSubmission(
    DownloadProtocol Protocol,
    string Title,
    string Artist,
    string Album,
    string Source,
    string? Quality,
    string? Language,
    long Size,
    string OriginalLocator);

public sealed record PreparedTorrentSubmission(
    string Title,
    string Artist,
    string Album,
    string Source,
    string? Quality,
    string? Language,
    long Size,
    string OriginalLocator,
    string InfoHash,
    byte[]? TorrentBytes,
    string? MagnetUri,
    string? FileName,
    IReadOnlyList<string> TrackerUrls)
    : PreparedDownloadSubmission(
        DownloadProtocol.Torrent,
        Title,
        Artist,
        Album,
        Source,
        Quality,
        Language,
        Size,
        OriginalLocator);

public sealed record PreparedUsenetSubmission(
    string Title,
    string Artist,
    string Album,
    string Source,
    string? Quality,
    string? Language,
    long Size,
    string OriginalLocator,
    byte[] NzbBytes,
    string FileName)
    : PreparedDownloadSubmission(
        DownloadProtocol.Usenet,
        Title,
        Artist,
        Album,
        Source,
        Quality,
        Language,
        Size,
        OriginalLocator);

public sealed record PreparedDirectDownloadSubmission(
    string Title,
    string Artist,
    string Album,
    string Source,
    string? Quality,
    string? Language,
    long Size,
    string OriginalLocator,
    Uri DownloadUri)
    : PreparedDownloadSubmission(
        DownloadProtocol.DirectDownload,
        Title,
        Artist,
        Album,
        Source,
        Quality,
        Language,
        Size,
        OriginalLocator);

public sealed record DownloadClientSubmissionResult(
    string ExternalId,
    string? ContentId = null,
    bool WasDuplicate = false);

public sealed class DownloadReferenceException : Exception
{
    public DownloadReferenceException(string message, bool expired = false, Exception? innerException = null)
        : base(message, innerException)
    {
        IsExpired = expired;
    }

    public bool IsExpired { get; }
}
