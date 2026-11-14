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

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent;

internal static class QbittorrentCompatibilityDetector
{
    public static async Task<List<Dictionary<string, JsonElement>>?> RecoverSelectedFilesAsync(
        HttpClient httpClient,
        string baseUrl,
        Dictionary<string, JsonElement>? properties,
        string hash,
        List<Dictionary<string, JsonElement>> files,
        CancellationToken ct)
    {
        if (!HasRdtClientSignature(properties))
        {
            return null;
        }

        // qBittorrent exposes these properties as torrent metadata, so they cannot
        // identify its server. Confirm rdt-client's native API on the same session.
        try
        {
            using var response = await httpClient.GetAsync($"{baseUrl}/Api/Settings/Version", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!(document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                && Version.TryParse(version.GetString(), out _)))
            {
                return null;
            }

            using var listResponse = await httpClient.GetAsync($"{baseUrl}/Api/Torrents", ct);
            if (!listResponse.IsSuccessStatusCode) return null;
            using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(ct));
            if (list.RootElement.ValueKind != JsonValueKind.Array) return null;
            var matches = list.RootElement.EnumerateArray().Where(torrent =>
                string.Equals(GetString(torrent, "hash"), hash, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || !Guid.TryParse(GetString(matches[0], "torrentId"), out var id)) return null;

            using var detailResponse = await httpClient.GetAsync($"{baseUrl}/Api/Torrents/Get/{id:D}", ct);
            if (!detailResponse.IsSuccessStatusCode) return null;
            using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(ct));
            var native = detail.RootElement;
            if (!string.Equals(GetString(native, "hash"), hash, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(GetString(native, "torrentId"), out var detailId) || detailId != id
                || !HasCompleted(native)
                || !native.TryGetProperty("files", out var nativeFiles) || nativeFiles.ValueKind != JsonValueKind.Array
                || !native.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return ReconcileSelectedFiles(files, nativeFiles, downloads);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static List<Dictionary<string, JsonElement>>? ReconcileSelectedFiles(
        List<Dictionary<string, JsonElement>> files, JsonElement nativeFiles, JsonElement downloads)
    {
        var reported = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!file.TryGetValue("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String
                || nameElement.GetString() is not { Length: > 0 } name
                || !reported.TryAdd(name, file)) return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var recovered = new List<Dictionary<string, JsonElement>>();
        foreach (var file in nativeFiles.EnumerateArray())
        {
            var name = GetString(file, "path");
            if (string.IsNullOrEmpty(name) || !seen.Add(name)
                || !file.TryGetProperty("selected", out var selected)
                || selected.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            if (!selected.GetBoolean()) continue;
            if (!reported.TryGetValue(name, out var original)
                || !file.TryGetProperty("bytes", out var bytesElement) || bytesElement.ValueKind != JsonValueKind.Number
                || !bytesElement.TryGetInt64(out var bytes) || bytes <= 0
                || original.TryGetValue("size", out var size)
                    && (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var reportedBytes) || reportedBytes != bytes)) return null;

            // Native Download.Path is a provider identifier/restricted link, not a disk path.
            // Correlate the explicit link; never infer identity from a URL/file basename.
            // A completed historical record cannot override selected=false above.
            var relative = name.Replace('\\', '/');
            if (relative.StartsWith('/') || relative.Split('/').Any(segment => segment is "" or "." or "..")
                || relative.Contains(':')) return null;
            var link = GetString(file, "downloadLink");
            if (string.IsNullOrEmpty(link)) return null;
            var records = downloads.EnumerateArray().Where(download =>
                string.Equals(GetString(download, "path"), link, StringComparison.Ordinal)).ToList();
            if (records.Count != 1 || !HasCompleted(records[0])) return null;

            var recoveredFile = new Dictionary<string, JsonElement>(original)
            {
                ["priority"] = JsonSerializer.SerializeToElement(1),
                ["progress"] = JsonSerializer.SerializeToElement(1),
                ["size"] = JsonSerializer.SerializeToElement(bytes)
            };
            recovered.Add(recoveredFile);
        }

        // Do not import an authoritative selection from a partial/foreign projection.
        return reported.Keys.All(seen.Contains) ? recovered : null;
    }

    private static bool HasCompleted(JsonElement value) =>
        DateTimeOffset.TryParse(GetString(value, "completed"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _)
        && (!value.TryGetProperty("error", out var error)
            || error.ValueKind == JsonValueKind.Null || error.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(error.GetString()));

    private static string? GetString(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var element)
            && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    internal static bool HasRdtClientSignature(Dictionary<string, JsonElement>? properties)
    {
        // rdt-client hard-codes both properties; app/version imitates qBittorrent.
        const string signature = "RealDebridClient <https://github.com/rogerfar/rdt-client>";
        return properties != null
            && properties.TryGetValue("created_by", out var creator)
            && creator.ValueKind == JsonValueKind.String
            && creator.GetString() == signature
            && properties.TryGetValue("comment", out var comment)
            && comment.ValueKind == JsonValueKind.String
            && comment.GetString() == signature;
    }
}
