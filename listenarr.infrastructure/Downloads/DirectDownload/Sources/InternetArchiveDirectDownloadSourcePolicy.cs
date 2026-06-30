// Listenarr - Audiobook Management System
// Copyright (C) 2024-2026 Listenarr Contributors

namespace Listenarr.Infrastructure.Downloads.DirectDownload.Sources;

internal sealed class InternetArchiveDirectDownloadSourcePolicy : IDirectDownloadSourcePolicy
{
    public int Priority => 0;
    public string Key => "InternetArchive";

    public bool CanPrepare(
        Indexer indexer,
        TrustedDownloadCandidate candidate,
        Uri uri) =>
        indexer.IsEnabled &&
        string.Equals(indexer.Implementation, "InternetArchive", StringComparison.OrdinalIgnoreCase) &&
        candidate.SourceDescriptor.Protocol == DownloadProtocol.DirectDownload &&
        TryValidateInitialUri(uri, out _);

    public bool TryValidateInitialUri(Uri uri, out string error)
    {
        if (!TryValidateExternalHttpUri(uri, out error))
        {
            return false;
        }

        // Internet Archive DDLs must start from the public /download route. Redirects
        // are validated separately because IA storage hosts may use different paths.
        if (!IsInternetArchiveHost(uri.Host) ||
            !uri.AbsolutePath.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
        {
            error = "The direct-download URL is not a trusted Internet Archive download.";
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
}
