/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Downloads.Submission
{
    /// <summary>
    /// Writes the blocklist entry for a download that failed. Shared by the external-client monitor
    /// and the direct-download worker so both block under the identity stamped at the grab and
    /// neither works one out for itself. The caller owns the failed-download-handling setting.
    /// </summary>
    public static class FailedDownloadBlocker
    {
        public static async Task BlockAsync(
            IBlocklistService blocklistService,
            Download download,
            string errorMessage)
        {
            if (!download.AudiobookId.HasValue)
            {
                return;
            }

            // Read back the identity stamped on the download when it was grabbed. By the time a
            // download fails its TotalSize has been overwritten and its OriginalUrl may be a spent
            // per-fetch link, so anything derived here disagrees with what the search side derives
            // from the indexer's listing and the row never matches.
            var identifier = ReleaseIdentity.ForGrabbed(download);
            if (identifier is null)
            {
                return;
            }

            await blocklistService.BlockAsync(
                download.AudiobookId.Value,
                identifier,
                download.Title ?? "Unknown",
                download.ExpectedFileSize ?? (download.TotalSize > 0 ? download.TotalSize : null),
                errorMessage);
        }
    }
}
