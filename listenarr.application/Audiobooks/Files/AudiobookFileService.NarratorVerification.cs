/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 */
using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    /// <summary>
    /// Flags a non-blocking History/toast warning when an audiobook has a RequiredNarrator set
    /// but the file being registered carries a conflicting embedded narrator tag (read from the
    /// MP4/ID3 "composer"/"narrator" field by <c>FfprobeTagMetadataMapper</c>). Never blocks the
    /// import - absent or matching tags are silently accepted, since many files have no embedded
    /// narrator tag at all.
    /// </summary>
    private async Task CheckNarratorMismatchAsync(
        Audiobook audiobook,
        AudioMetadata? meta,
        string filePath,
        string? source)
    {
        if (string.IsNullOrWhiteSpace(audiobook.RequiredNarrator)
            || string.IsNullOrWhiteSpace(meta?.Narrator)
            || NarratorNameMatcher.Contains(meta.Narrator, audiobook.RequiredNarrator)
            || NarratorNameMatcher.Contains(audiobook.RequiredNarrator, meta.Narrator))
        {
            return;
        }

        var audiobookTitle = audiobook.Title ?? "Unknown";
        logger.LogWarning(
            "Imported file's embedded narrator tag does not match the required narrator. AudiobookId={AudiobookId} Required={Required} Embedded={Embedded} File={File}",
            audiobook.Id, audiobook.RequiredNarrator, meta.Narrator, LogRedaction.SanitizeFilePath(filePath));

        try
        {
            var historyEntry = new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobookTitle,
                EventType = "Narrator Mismatch",
                Message = $"Imported file is narrated by '{meta.Narrator}' but '{audiobook.RequiredNarrator}' was required: {Path.GetFileName(filePath)}",
                Source = source ?? "Scan",
                Data = JsonSerializer.Serialize(new { FilePath = filePath, RequiredNarrator = audiobook.RequiredNarrator, EmbeddedNarrator = meta.Narrator }),
                Timestamp = DateTime.UtcNow
            };
            await historyRepository.AddAsync(historyEntry);

            try
            {
                await toastService.PublishToastAsync("warning", "Narrator mismatch", $"{audiobookTitle}: expected '{audiobook.RequiredNarrator}', got '{meta.Narrator}'");
            }
            catch (Exception thx) when (thx is not OperationCanceledException && thx is not OutOfMemoryException && thx is not StackOverflowException)
            {
                logger.LogDebug(thx, "Failed to publish toast for narrator mismatch");
            }
        }
        catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException)
        {
            logger.LogDebug(hx, "Failed to record narrator mismatch history entry");
        }
    }
}
