// Listenarr - Audiobook Management System
// Copyright (C) 2024-2026 Listenarr Contributors

namespace Listenarr.Infrastructure.Downloads.DirectDownload.Sources;

public interface IDirectDownloadSourcePolicy
{
    int Priority { get; }
    string Key { get; }

    bool CanPrepare(
        Indexer indexer,
        TrustedDownloadCandidate candidate,
        Uri uri);

    bool TryValidateInitialUri(Uri uri, out string error);

    bool TryValidateRedirectUri(Uri uri, Uri previousUri, out string error);

    string GetFileName(Uri uri, Download download);
}
