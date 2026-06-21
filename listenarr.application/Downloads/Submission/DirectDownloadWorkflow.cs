/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Submission
{
    public sealed class DirectDownloadWorkflow(
        IDownloadRepository downloadRepository,
        ILogger<DirectDownloadWorkflow> logger)
    {
        public async Task<string> CreateTrackedDownloadAsync(
            PreparedDirectDownloadSubmission submission,
            int? audiobookId)
        {
            try
            {
                var id = Guid.NewGuid().ToString();
                var download = new Download
                {
                    Id = id,
                    AudiobookId = audiobookId,
                    Title = submission.Title,
                    Artist = submission.Artist,
                    Album = submission.Album,
                    Language = submission.Language,
                    OriginalUrl = submission.DownloadUri.ToString(),
                    Progress = 0,
                    TotalSize = submission.Size,
                    DownloadedSize = 0,
                    DownloadPath = string.Empty,
                    FinalPath = string.Empty,
                    StartedAt = DateTime.UtcNow,
                    DownloadClientId = "DDL",
                    Metadata = new Dictionary<string, object>
                    {
                        ["Source"] = submission.Source,
                        ["Quality"] = submission.Quality ?? string.Empty,
                        ["Language"] = submission.Language ?? string.Empty,
                        ["DownloadType"] = "DDL"
                    }
                };

                await downloadRepository.AddAsync(download);
                return id;
            }
            catch (UniqueConstraintViolationException) when (audiobookId.HasValue)
            {
                logger.LogInformation(
                    "Concurrent duplicate direct download prevented for audiobook {AudiobookId}",
                    audiobookId);
                return string.Empty;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Failed to persist direct-download reservation");
                throw new PersistenceException("Failed to persist direct-download reservation.", ex);
            }
        }
    }
}
