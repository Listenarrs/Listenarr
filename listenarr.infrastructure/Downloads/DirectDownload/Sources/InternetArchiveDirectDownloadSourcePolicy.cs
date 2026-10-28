/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Infrastructure.Downloads.DirectDownload.Sources;

internal sealed class InternetArchiveDirectDownloadSourcePolicy : IDirectDownloadSourcePolicy
{
    public int Priority => 0;
    public string Key => "InternetArchive";

    public bool CanPrepare(
        Indexer indexer,
        TrustedDownloadCandidate candidate,
        IReadOnlyList<Uri> uris) =>
        indexer.IsEnabled &&
        string.Equals(indexer.Implementation, "InternetArchive", StringComparison.OrdinalIgnoreCase) &&
        candidate.SourceDescriptor.Protocol == DownloadProtocol.DirectDownload &&
        TryValidateArtifactPlan(uris, out _);

    public bool TryValidateArtifactPlan(IReadOnlyList<Uri> uris, out string error)
    {
        if (uris.Count == 0)
        {
            error = "The direct-download artifact plan is empty.";
            return false;
        }

        foreach (var uri in uris)
        {
            if (!TryValidateInitialUri(uri, out error))
            {
                return false;
            }
        }

        if (uris.Select(GetItemIdentifier).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            error = "All direct-download artifacts must belong to the same Internet Archive item.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TryValidateInitialUri(Uri uri, out string error)
    {
        if (!TryValidateExternalHttpUri(uri, out error))
        {
            return false;
        }

        // Internet Archive DDLs must start from the public /download route. Redirects
        // are validated separately because IA storage hosts may use different paths.
        if (!IsInternetArchiveHost(uri.Host) ||
            !TryGetDownloadPathParts(uri, out _, out _))
        {
            error = "The direct-download URL is not a trusted Internet Archive artifact.";
            return false;
        }

        return true;
    }

    public bool TryValidateRedirectUri(Uri uri, Uri previousUri, out string error)
    {
        if (!TryValidateExternalHttpUri(uri, out error))
        {
            return false;
        }

        if (!IsInternetArchiveHost(uri.Host))
        {
            error = "The direct-download redirect target is not a trusted Internet Archive host.";
            return false;
        }

        return true;
    }

    public string GetFileName(Uri uri, Download download)
    {
        var fileName = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
        return string.IsNullOrWhiteSpace(fileName)
            ? $"{download.Title}.download"
            : fileName;
    }

    private static bool TryValidateExternalHttpUri(Uri uri, out string error)
    {
        if (!OutboundRequestSecurity.TryValidateExternalHttpUri(uri, out var validationError, allowPrivateTargets: false))
        {
            error = $"The direct-download URL is not allowed: {validationError}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsInternetArchiveHost(string host) =>
        string.Equals(host, "archive.org", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase);

    private static string GetItemIdentifier(Uri uri) =>
        TryGetDownloadPathParts(uri, out var identifier, out _)
            ? identifier
            : string.Empty;

    private static bool TryGetDownloadPathParts(
        Uri uri,
        out string itemIdentifier,
        out string artifactFileName)
    {
        itemIdentifier = string.Empty;
        artifactFileName = string.Empty;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 ||
            !string.Equals(segments[0], "download", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        itemIdentifier = Uri.UnescapeDataString(segments[1]).Trim();
        artifactFileName = Uri.UnescapeDataString(segments[^1]).Trim();
        return !string.IsNullOrWhiteSpace(itemIdentifier) &&
            !string.IsNullOrWhiteSpace(artifactFileName) &&
            artifactFileName is not "." and not "..";
    }
}
