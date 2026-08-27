/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdConnectionTester(
        IHttpClientFactory httpFactory,
        SabnzbdRequestBuilder requestBuilder,
        ILogger<SabnzbdAdapter> logger,
        string clientType)
    {
        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(client);
                var requestContext = requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    return (false, "SABnzbd API key not configured in client settings");
                }

                var url = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "version",
                    ["output"] = "json"
                });
                var http = httpFactory.CreateClient(clientType);
                var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return (false, "SABnzbd: API key invalid or unauthorized");
                    }

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        return (false, "SABnzbd: host or endpoint not found (check host/port)");
                    }

                    return (false, $"SABnzbd: returned {resp.StatusCode}");
                }

                // Version check passed. If a category is configured, verify it exists in
                // SABnzbd: unknown categories are silently reassigned to Default, which
                // hides jobs from category-scoped reads and strands them unimported.
                // This is an advisory, not a hard failure, so the connection tests green.
                var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
                if (string.IsNullOrWhiteSpace(configuredCategory))
                {
                    return (true, "SABnzbd: connected");
                }

                var categoryWarning = await CheckCategoryExistsAsync(requestContext, http, configuredCategory, ct);
                return categoryWarning is null
                    ? (true, "SABnzbd: connected")
                    : (true, categoryWarning);
            }
            catch (HttpRequestException httpEx)
            {
                logger.LogDebug(httpEx, "SABnzbd TestConnection network error");
                return (false, $"SABnzbd: network error ({httpEx.StatusCode?.ToString() ?? "unavailable"})");
            }
            catch (TaskCanceledException tce)
            {
                logger.LogDebug(tce, "SABnzbd TestConnection timed out");
                return (false, "SABnzbd: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "SABnzbd TestConnection failed");
                return (false, "SABnzbd: connection failed");
            }
        }

        private async Task<string?> CheckCategoryExistsAsync(
            SabnzbdRequestContext requestContext,
            HttpClient http,
            string configuredCategory,
            CancellationToken ct)
        {
            try
            {
                var url = requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "get_cats",
                    ["output"] = "json"
                });
                var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // Best effort: an unavailable category list must not fail an
                    // otherwise healthy connection.
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("categories", out var categories) ||
                    categories.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var category in categories.EnumerateArray())
                {
                    var name = category.ValueKind == JsonValueKind.String ? category.GetString() : null;
                    if (string.Equals(name?.Trim(), configuredCategory.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                }

                return $"SABnzbd: connected, but category '{configuredCategory}' does not exist in SABnzbd. " +
                       "Jobs will fall into Default and may not import. Create the category in SABnzbd (Config > Categories).";
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "SABnzbd get_cats probe failed (non-fatal)");
                return null;
            }
        }
    }
}
