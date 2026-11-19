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
using System.Text.Json;

namespace Listenarr.Application.Downloads.Submission
{
    public sealed class DirectDownloadWorkflow(
        IDownloadRepository downloadRepository,
        ILogger<DirectDownloadWorkflow> logger)
    {
        public async Task<string> CreateTrackedDownloadAsync(
            PreparedDirectDownloadSubmission submission,
            int? audiobookId,
            string? releaseIdentifier = null)
        {
            if (submission.Artifacts.Count == 0)
            {
                throw new DownloadClientSubmissionException(
                    "The direct-download submission does not contain any artifacts.");
            }

            try
            {
                var primaryArtifact = submission.Artifacts[0];
                var artifactPlan = submission.Artifacts
                    .Select(artifact => new PersistedDirectDownloadArtifact(
                        artifact.DownloadUri.ToString(),
                        artifact.FileName,
                        artifact.ExpectedSize,
                        artifact.Packaging))
                    .ToList();
                var id = Guid.NewGuid().ToString();
                var download = new Download
                {
                    Id = id,
                    AudiobookId = audiobookId,
                    Title = submission.Title,
                    Artist = submission.Artist,
                    Album = submission.Album,
                    Language = submission.Language,
                    OriginalUrl = primaryArtifact.DownloadUri.ToString(),
                    Progress = 0,
                    TotalSize = submission.Size,
                    // TotalSize is not usable as an identity input here: DirectDownloadProcessor
                    // raises it three separate times as bytes arrive. ExpectedFileSize holds the
                    // advertised size unchanged.
                    ExpectedFileSize = submission.Size,
                    DownloadedSize = 0,
                    DownloadPath = string.Empty,
                    FinalPath = string.Empty,
                    StartedAt = DateTime.UtcNow,
                    DownloadClientId = DirectDownloadMetadataKeys.ClientId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Source"] = submission.Source,
                        ["Quality"] = submission.Quality ?? string.Empty,
                        ["Language"] = submission.Language ?? string.Empty,
                        [DirectDownloadMetadataKeys.DownloadType] = DirectDownloadMetadataKeys.ClientId,
                        // The worker revalidates this policy before every HTTP request so
                        // future DDL sources stay additive without making Listenarr fetch arbitrary URLs.
                        [DirectDownloadMetadataKeys.SourcePolicyKey] = submission.SourcePolicyKey,
                        [DirectDownloadMetadataKeys.OriginalHost] = primaryArtifact.DownloadUri.Host,
                        [DirectDownloadMetadataKeys.ArtifactPlan] = JsonSerializer.Serialize(
                            new PersistedDirectDownloadArtifactPlan(
                                PersistedDirectDownloadArtifactPlan.CurrentVersion,
                                artifactPlan)),
                        [DirectDownloadMetadataKeys.RequiresArchiveExtraction] = artifactPlan.Any(
                            artifact => artifact.Packaging == DirectDownloadArtifactPackaging.Archive)
                    }
                };

                // Same stamp as the client-backed path in DownloadRecordFactory. A direct download
                // that fails has to be blockable by the identity the search result was grabbed
                // under, or the automatic search picks the same dead link up again.
                if (!string.IsNullOrWhiteSpace(releaseIdentifier))
                {
                    download.SetMetadata(ReleaseIdentity.MetadataKey, releaseIdentifier);
                }

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
