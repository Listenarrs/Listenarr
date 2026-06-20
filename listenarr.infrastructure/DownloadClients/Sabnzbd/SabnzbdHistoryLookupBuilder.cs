/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal readonly record struct SabnzbdCompletedHistoryItem(
        string Name,
        string Status,
        string Path,
        DateTime CompletedTime,
        string NzoId);

    internal readonly record struct SabnzbdFailedHistoryItem(
        string Name,
        string Status,
        string Path,
        DateTime CompletedTime,
        string NzoId,
        string Error);

    internal sealed record SabnzbdHistoryLookup(
        List<SabnzbdCompletedHistoryItem> CompletedItems,
        List<SabnzbdFailedHistoryItem> FailedItems);

    internal static class SabnzbdHistoryLookupBuilder
    {
        public static SabnzbdHistoryLookup Build(JsonElement slots, ILogger logger)
        {
            var completedItems = new List<SabnzbdCompletedHistoryItem>();
            var failedItems = new List<SabnzbdFailedHistoryItem>();

            foreach (var slot in slots.EnumerateArray())
            {
                var name = slot.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var status = slot.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                var path = slot.TryGetProperty("storage", out var pathProp) ? pathProp.GetString() ?? "" : "";
                var nzoId = slot.TryGetProperty("nzo_id", out var nzoIdProp) ? nzoIdProp.GetString() ?? "" : "";

                var completedTime = DateTime.MinValue;
                if (slot.TryGetProperty("completed", out var completedProp))
                {
                    var completedTimestamp = completedProp.GetInt64();
                    completedTime = DateTimeOffset.FromUnixTimeSeconds(completedTimestamp).DateTime;
                }

                if (!string.IsNullOrEmpty(name) &&
                    (status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("Complete", StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogInformation("SABnzbd history slot parsed: nzo_id={NzoId}, name={Name}, status={Status}, path={Path}, completed={Completed}", nzoId, LogRedaction.SanitizeText(name), LogRedaction.SanitizeText(status), LogRedaction.SanitizeFilePath(path), completedTime);
                    completedItems.Add(new SabnzbdCompletedHistoryItem(name, status, path, completedTime, nzoId));
                }
                else if (!string.IsNullOrEmpty(name) && status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    var failMessage = slot.TryGetProperty("fail_message", out var failProp)
                        ? failProp.GetString() ?? string.Empty
                        : status;

                    failedItems.Add(new SabnzbdFailedHistoryItem(name, status, path, completedTime, nzoId, failMessage));
                }
            }

            return new SabnzbdHistoryLookup(completedItems, failedItems);
        }
    }
}
