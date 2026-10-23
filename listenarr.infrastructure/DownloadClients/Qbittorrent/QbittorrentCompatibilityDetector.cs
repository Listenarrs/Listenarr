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
    public static async Task<bool> IsRdtClientAsync(
        HttpClient httpClient,
        string baseUrl,
        Dictionary<string, JsonElement>? properties,
        CancellationToken ct)
    {
        if (!HasRdtClientSignature(properties))
        {
            return false;
        }

        // qBittorrent exposes these properties as torrent metadata, so they cannot
        // identify its server. Confirm rdt-client's native API on the same session.
        try
        {
            using var response = await httpClient.GetAsync($"{baseUrl}/Api/Settings/Version", ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                && Version.TryParse(version.GetString(), out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

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
