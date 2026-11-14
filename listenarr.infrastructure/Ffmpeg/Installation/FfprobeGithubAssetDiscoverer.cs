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

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    internal sealed class FfprobeGithubAssetDiscoverer
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public FfprobeGithubAssetDiscoverer(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<(string? AssetUrl, string? ChecksumContent)> TryDiscoverAsync(string repo, string? releaseOverride, string? arch)
        {
            try
            {
                // Use GitHub Releases API: https://api.github.com/repos/{owner}/{repo}/releases
                var releasesUrl = $"https://api.github.com/repos/{repo}/releases";
                using var req = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
                req.Headers.Add("User-Agent", "Listenarr-Installer");
                using var resp = await _httpClient.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                var docs = JsonSerializer.Deserialize<JsonElement>(body);
                if (docs.ValueKind != JsonValueKind.Array) return (null, null);

                foreach (var release in docs.EnumerateArray())
                {
                    var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(releaseOverride) && !tag.Contains(releaseOverride, StringComparison.OrdinalIgnoreCase)) continue;
                    if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        string? checksumContent = null;
                        string? chosenUrl = null;
                        // First, attempt to find checksum asset(s)
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.GetProperty("name").GetString() ?? string.Empty;
                            var url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                            if (string.IsNullOrEmpty(url)) continue;
                            if (name.Contains("sha256", StringComparison.OrdinalIgnoreCase) || name.Contains("checksum", StringComparison.OrdinalIgnoreCase) || name.Contains("sha256sums", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var c = await (await _httpClient.GetAsync(url)).Content.ReadAsStringAsync();
                                    if (!string.IsNullOrEmpty(c)) checksumContent = c;
                                }
                                catch (Exception caughtEx_10) when (caughtEx_10 is not OperationCanceledException && caughtEx_10 is not OutOfMemoryException && caughtEx_10 is not StackOverflowException)
                                {
                                    _logger.LogDebug(caughtEx_10, "Could not download the checksum asset at {Url}; continuing without it", url);
                                }
                            }
                        }

                        // Then find a matching asset for platform/arch
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.GetProperty("name").GetString() ?? string.Empty;
                            var url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                            if (string.IsNullOrEmpty(url)) continue;
                            if (!string.IsNullOrEmpty(arch) && !name.Contains(arch, StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                            {
                                chosenUrl = url;
                                break;
                            }
                        }

                        if (!string.IsNullOrEmpty(chosenUrl)) return (chosenUrl, checksumContent);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "GitHub asset discovery failed for repo {Repo}", repo);
            }

            return (null, null);
        }
    }
}
