/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdRequestBuilder
    {
        public SabnzbdRequestContext CreateContext(DownloadClientConfiguration client)
        {
            var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/api").ToString();
            var apiKey = "";
            if (client.Settings != null && client.Settings.TryGetValue("apiKey", out var apiKeyObj))
            {
                apiKey = apiKeyObj?.ToString() ?? "";
            }

            return new SabnzbdRequestContext(baseUrl, apiKey);
        }

        public string BuildUrl(SabnzbdRequestContext context, IReadOnlyDictionary<string, string> queryParams)
        {
            var merged = new Dictionary<string, string>(queryParams, StringComparer.OrdinalIgnoreCase)
            {
                ["apikey"] = context.ApiKey
            };
            var queryString = string.Join("&", merged.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            return $"{context.BaseUrl}?{queryString}";
        }

        public List<string> BuildSensitiveValues(SabnzbdRequestContext context, string? indexerApiKey = null)
        {
            var sensitiveValues = LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { context.ApiKey }).ToList();
            if (!string.IsNullOrEmpty(indexerApiKey))
            {
                sensitiveValues.Add(indexerApiKey);
            }

            return sensitiveValues;
        }
    }

    internal sealed record SabnzbdRequestContext(string BaseUrl, string ApiKey)
    {
        public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
    }
}
