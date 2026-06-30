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

internal interface IDirectDownloadSourcePolicy
{
    int Priority { get; }
    string Key { get; }

    bool CanPrepare(
        Indexer indexer,
        TrustedDownloadCandidate candidate,
        IReadOnlyList<Uri> uris);

    bool TryValidateArtifactPlan(IReadOnlyList<Uri> uris, out string error);

    bool TryValidateInitialUri(Uri uri, out string error);

    bool TryValidateRedirectUri(Uri uri, Uri previousUri, out string error);

    string GetFileName(Uri uri, Download download);
}
