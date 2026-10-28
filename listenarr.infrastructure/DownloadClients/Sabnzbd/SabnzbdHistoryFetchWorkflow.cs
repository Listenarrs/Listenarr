/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdHistoryFetchWorkflow(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger<SabnzbdAdapter> logger,
        string clientType)
    {
        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var result = new List<(string Id, string Name)>();
            if (client == null) return result;

            try
            {
                var requestContext = requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey) return result;

                var historyUrl = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "history",
                    ["output"] = "json",
                    ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
                });
                var http = httpFactory.CreateClient(clientType);
                var historyResp = await http.GetAsync(historyUrl, ct);
                if (!historyResp.IsSuccessStatusCode) return result;

                var historyText = await historyResp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(historyText)) return result;

                var doc = JsonDocument.Parse(historyText);
                if (doc.RootElement.TryGetProperty("history", out var history) && history.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                {
                    foreach (var slot in slots.EnumerateArray())
                    {
                        var nzoId = slot.TryGetProperty("nzo_id", out var nzo) ? nzo.GetString() ?? string.Empty : string.Empty;
                        var name = slot.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty;
                        result.Add((nzoId, name));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to fetch SABnzbd history (non-fatal)");
            }

            return result;
        }
    }
}
