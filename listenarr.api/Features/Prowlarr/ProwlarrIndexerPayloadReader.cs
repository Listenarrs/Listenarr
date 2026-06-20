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

namespace Listenarr.Api.Features.Prowlarr
{
    internal static class ProwlarrIndexerPayloadReader
    {
        public static ParsedProwlarrIndexerPayload ParseForPut(JsonElement payload)
        {
            var parsed = ParseCommon(payload);
            return parsed with { Url = BuildUrl(parsed.BaseUrl, parsed.ApiPath) };
        }

        public static ParsedProwlarrIndexerPayload ParseForBulkPost(JsonElement payload)
        {
            var parsed = ParseTopLevel(payload);

            if (!string.IsNullOrEmpty(parsed.ApiPath) && !string.IsNullOrEmpty(parsed.BaseUrl))
            {
                parsed = parsed with { BaseUrl = BuildUrl(parsed.BaseUrl, parsed.ApiPath) };
            }

            parsed = ApplySettingsFallback(payload, parsed);
            parsed = ApplyFieldsFallback(payload, parsed, onlyMissingValues: true);

            if (string.IsNullOrEmpty(parsed.Name))
            {
                parsed = parsed with { Name = parsed.BaseUrl ?? "Prowlarr Indexer" };
            }

            if (string.IsNullOrEmpty(parsed.Implementation))
            {
                parsed = parsed with { Implementation = "Custom" };
            }

            return parsed with { Url = (parsed.BaseUrl ?? string.Empty).Trim() };
        }

        private static ParsedProwlarrIndexerPayload ParseCommon(JsonElement payload)
        {
            var parsed = ParseTopLevel(payload);
            parsed = ApplySettingsFallback(payload, parsed);
            parsed = ApplyFieldsFallback(payload, parsed, onlyMissingValues: false);

            if (string.IsNullOrEmpty(parsed.Name))
            {
                parsed = parsed with { Name = string.IsNullOrEmpty(parsed.BaseUrl) ? "Prowlarr Indexer" : parsed.BaseUrl };
            }

            if (string.IsNullOrEmpty(parsed.Implementation))
            {
                parsed = parsed with { Implementation = "Custom" };
            }

            return parsed;
        }

        private static ParsedProwlarrIndexerPayload ParseTopLevel(JsonElement payload)
        {
            return new ParsedProwlarrIndexerPayload(
                Name: GetStringProperty(payload, "name", "title"),
                Implementation: GetStringProperty(payload, "implementation", "type"),
                BaseUrl: GetStringProperty(payload, "baseUrl", "url"),
                ApiPath: GetStringProperty(payload, "apiPath", null),
                ApiKey: GetStringProperty(payload, "apiKey", null),
                Categories: ParseCategories(payload),
                Url: string.Empty);
        }

        private static ParsedProwlarrIndexerPayload ApplySettingsFallback(JsonElement payload, ParsedProwlarrIndexerPayload parsed)
        {
            if (!payload.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            {
                return parsed;
            }

            var baseUrl = parsed.BaseUrl;
            var apiKey = parsed.ApiKey;
            var apiPath = parsed.ApiPath;

            if (string.IsNullOrEmpty(baseUrl)) baseUrl = GetStringProperty(settings, "baseUrl", "url");
            if (string.IsNullOrEmpty(apiKey)) apiKey = GetStringProperty(settings, "apiKey", "apikey");
            if (string.IsNullOrEmpty(apiPath)) apiPath = GetStringProperty(settings, "apiPath", null);

            return parsed with { BaseUrl = baseUrl, ApiKey = apiKey, ApiPath = apiPath };
        }

        private static ParsedProwlarrIndexerPayload ApplyFieldsFallback(JsonElement payload, ParsedProwlarrIndexerPayload parsed, bool onlyMissingValues)
        {
            if (!payload.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            {
                return parsed;
            }

            var baseUrl = parsed.BaseUrl;
            var apiKey = parsed.ApiKey;
            var apiPath = parsed.ApiPath;
            var categories = parsed.Categories;

            foreach (var field in fields.EnumerateArray().Where(field => field.ValueKind == JsonValueKind.Object))
            {
                var name = GetStringProperty(field, "name", null);
                if (string.IsNullOrEmpty(name)) continue;

                if ((!onlyMissingValues || string.IsNullOrEmpty(baseUrl)) &&
                    name.Equals("baseUrl", StringComparison.InvariantCultureIgnoreCase) &&
                    field.TryGetProperty("value", out var baseUrlValue) &&
                    baseUrlValue.ValueKind == JsonValueKind.String)
                {
                    baseUrl = baseUrlValue.GetString() ?? baseUrl;
                }

                if ((!onlyMissingValues || string.IsNullOrEmpty(apiKey)) &&
                    name.Equals("apiKey", StringComparison.InvariantCultureIgnoreCase) &&
                    field.TryGetProperty("value", out var apiKeyValue) &&
                    apiKeyValue.ValueKind == JsonValueKind.String)
                {
                    apiKey = apiKeyValue.GetString() ?? apiKey;
                }

                if ((!onlyMissingValues || string.IsNullOrEmpty(apiPath)) &&
                    name.Equals("apiPath", StringComparison.InvariantCultureIgnoreCase) &&
                    field.TryGetProperty("value", out var apiPathValue) &&
                    apiPathValue.ValueKind == JsonValueKind.String)
                {
                    apiPath = apiPathValue.GetString() ?? apiPath;
                }

                if ((!onlyMissingValues || string.IsNullOrEmpty(categories)) &&
                    name.Equals("categories", StringComparison.InvariantCultureIgnoreCase))
                {
                    categories = ParseFieldCategories(field, categories);
                }

                if (onlyMissingValues &&
                    !string.IsNullOrEmpty(baseUrl) &&
                    !string.IsNullOrEmpty(apiKey) &&
                    !string.IsNullOrEmpty(apiPath) &&
                    !string.IsNullOrEmpty(categories))
                {
                    break;
                }
            }

            return parsed with { BaseUrl = baseUrl, ApiKey = apiKey, ApiPath = apiPath, Categories = categories };
        }

        private static string BuildUrl(string? baseUrl, string? apiPath)
        {
            var url = (baseUrl ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(apiPath) && !string.IsNullOrEmpty(url))
            {
                url = url.TrimEnd('/') + "/" + apiPath.Trim('/');
            }

            return url;
        }

        private static string GetStringProperty(JsonElement el, string prop1, string? prop2 = null)
        {
            if (el.TryGetProperty(prop1, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString() ?? string.Empty;
            if (prop2 != null && el.TryGetProperty(prop2, out var p2) && p2.ValueKind == JsonValueKind.String)
                return p2.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static string? ParseCategories(JsonElement el)
        {
            if (el.TryGetProperty("categories", out var cats))
            {
                if (cats.ValueKind == JsonValueKind.Array)
                {
                    var parts = cats.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32().ToString() : x.GetString() ?? string.Empty)
                        .Where(s => !string.IsNullOrEmpty(s));
                    return string.Join(',', parts);
                }

                if (cats.ValueKind == JsonValueKind.String)
                {
                    return cats.GetString();
                }
            }

            return null;
        }

        private static string? ParseFieldCategories(JsonElement field, string? fallback)
        {
            if (field.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                var parts = value.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32().ToString() : x.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s));
                return string.Join(',', parts);
            }

            if (field.TryGetProperty("value", out var stringValue) && stringValue.ValueKind == JsonValueKind.String)
            {
                return stringValue.GetString() ?? fallback;
            }

            return fallback;
        }
    }

    public sealed record ParsedProwlarrIndexerPayload(
        string Name,
        string Implementation,
        string BaseUrl,
        string ApiPath,
        string ApiKey,
        string? Categories,
        string Url);
}
