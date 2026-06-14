/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    public sealed class DirectDownloadWorkflow(
        IDownloadRepository downloadRepository,
        ILogger<DirectDownloadWorkflow> logger)
    {
        public async Task<string> CreateTrackedDownloadAsync(SearchResult searchResult, int? audiobookId)
        {
            try
            {
                var id = Guid.NewGuid().ToString();
                var download = new Download
                {
                    Id = id,
                    AudiobookId = audiobookId,
                    Title = searchResult.Title,
                    Language = searchResult.Language,
                    OriginalUrl = searchResult.TorrentUrl ?? searchResult.NzbUrl ?? searchResult.MagnetLink ?? string.Empty,
                    Progress = 0,
                    TotalSize = searchResult.Size,
                    DownloadedSize = 0,
                    DownloadPath = string.Empty,
                    FinalPath = string.Empty,
                    StartedAt = DateTime.UtcNow,
                    DownloadClientId = "DDL",
                    Metadata = new Dictionary<string, object>
                    {
                        ["Source"] = searchResult.Source ?? string.Empty,
                        ["Quality"] = searchResult.Quality ?? string.Empty,
                        ["Language"] = searchResult.Language ?? string.Empty,
                        ["DownloadType"] = "DDL"
                    }
                };

                await downloadRepository.AddAsync(download);
                return id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "DownloadDirectlyAsync: failed to create DDL download record");
                return Guid.NewGuid().ToString();
            }
        }
    }
}
