/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Indexers.MyAnonamouse
{
    public static partial class MyAnonamouseResponseParser
    {
        private static void PopulateDownloadLinks(
            JsonElement item,
            IndexerSearchResult result,
            string? downloadUrlField,
            string? infoUrlField,
            string? fileNameField,
            string title,
            string id,
            int debugIndex,
            ILogger logger)
        {
            // Robust link detection: prefer magnet/hash/torrent indicators, only treat as NZB when explicit NZB fields exist
            try
            {
                string magnetLink = "";
                // Common magnet field names
                if (item.TryGetProperty("magnet", out var magnetElem) && magnetElem.ValueKind == JsonValueKind.String)
                    magnetLink = magnetElem.GetString() ?? "";
                else if (item.TryGetProperty("magnetLink", out magnetElem) && magnetElem.ValueKind == JsonValueKind.String)
                    magnetLink = magnetElem.GetString() ?? "";
                else if (item.TryGetProperty("magnetlink", out magnetElem) && magnetElem.ValueKind == JsonValueKind.String)
                    magnetLink = magnetElem.GetString() ?? "";

                // If we have a torrent hash, construct a magnet link
                if (string.IsNullOrEmpty(magnetLink) && item.TryGetProperty("hash", out var hashElem) && hashElem.ValueKind == JsonValueKind.String)
                {
                    var h = hashElem.GetString();
                    if (!string.IsNullOrWhiteSpace(h))
                    {
                        magnetLink = $"magnet:?xt=urn:btih:{h}&dn={Uri.EscapeDataString(title)}";
                    }
                }

                // Detect torrent download URL from other common fields
                string[] torrentFields = new[] { "download", "dlLink", "downloadlink", "download_url", "torrent", "torrent_url", "torrentUrl", "torrentlink" };
                var torrentUrlDetected = result.TorrentUrl
                    ?? torrentFields
                        .Select(tf => item.TryGetProperty(tf, out var tfElem) && tfElem.ValueKind == JsonValueKind.String
                            ? tfElem.GetString()
                            : null)
                        .FirstOrDefault(url => !string.IsNullOrEmpty(url))
                    ?? string.Empty;

                // If any URL looks like a .torrent file, prefer it as torrent URL
                if (string.IsNullOrEmpty(torrentUrlDetected))
                {
                    foreach (var v in item.EnumerateObject()
                        .Where(prop => prop.Value.ValueKind == JsonValueKind.String)
                        .Select(prop => prop.Value.GetString())
                        .Where(v => !string.IsNullOrEmpty(v) && v.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)))
                    {
                        torrentUrlDetected = v!;
                        break;
                    }
                }

                // Detect NZB fields (only treat as NZB when explicit)
                string nzbUrlDetected = string.Empty;
                if (item.TryGetProperty("nzb", out var nzbElem) && nzbElem.ValueKind == JsonValueKind.String)
                    nzbUrlDetected = nzbElem.GetString() ?? string.Empty;
                else if (item.TryGetProperty("nzbLink", out nzbElem) && nzbElem.ValueKind == JsonValueKind.String)
                    nzbUrlDetected = nzbElem.GetString() ?? string.Empty;
                else if (item.TryGetProperty("nzburl", out nzbElem) && nzbElem.ValueKind == JsonValueKind.String)
                    nzbUrlDetected = nzbElem.GetString() ?? string.Empty;

                // Apply discovered links to the result
                if (!string.IsNullOrEmpty(magnetLink)) result.MagnetLink = magnetLink;
                if (!string.IsNullOrEmpty(torrentUrlDetected)) result.TorrentUrl = torrentUrlDetected;
                if (!string.IsNullOrEmpty(nzbUrlDetected)) result.NzbUrl = nzbUrlDetected;

                // If a direct downloadUrl was provided by the API, prefer that as the torrent/nzb URL
                if (!string.IsNullOrEmpty(downloadUrlField))
                {
                    // Choose disposition based on common hints and protocol
                    if (downloadUrlField.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || (item.TryGetProperty("protocol", out var protoElem) && protoElem.ValueKind == JsonValueKind.String && protoElem.GetString()?.Equals("torrent", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        result.TorrentUrl = downloadUrlField;
                    }
                    else if (downloadUrlField.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase) || (item.TryGetProperty("protocol", out var proto2Elem) && proto2Elem.ValueKind == JsonValueKind.String && proto2Elem.GetString()?.Equals("usenet", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        result.NzbUrl = downloadUrlField;
                    }
                    else
                    {
                        // Unknown, prefer TorrentUrl by default
                        result.TorrentUrl = downloadUrlField;
                    }
                }

                // If guid is present and looks like a URL, prefer it as the canonical link
                if (item.TryGetProperty("guid", out var guidElem) && guidElem.ValueKind == JsonValueKind.String && Uri.IsWellFormedUriString(guidElem.GetString(), UriKind.Absolute))
                {
                    result.ResultUrl = guidElem.GetString();
                }

                // If infoUrl is present, use it as the canonical page link when available
                if (!string.IsNullOrEmpty(infoUrlField))
                {
                    result.ResultUrl = infoUrlField;
                }

                // Use filename field to populate TorrentFileName when available
                if (!string.IsNullOrEmpty(fileNameField))
                {
                    result.TorrentFileName = fileNameField;
                }

                // Prefer marking the download type when either magnet/torrent or NZB URL exists
                if (!string.IsNullOrEmpty(result.MagnetLink) || !string.IsNullOrEmpty(result.TorrentUrl))
                    result.DownloadType = "Torrent";
                else if (!string.IsNullOrEmpty(result.NzbUrl))
                    result.DownloadType = "nzb";

                logger.LogDebug("MyAnonamouse parsed item #{Index} link-disposition: magnet={MagnetPresent}, torrent={TorrentPresent}, nzb={NzbPresent}", debugIndex, !string.IsNullOrEmpty(result.MagnetLink), !string.IsNullOrEmpty(result.TorrentUrl), !string.IsNullOrEmpty(result.NzbUrl));
            }
            catch (Exception exLink) when (exLink is not OperationCanceledException && exLink is not OutOfMemoryException && exLink is not StackOverflowException)
            {
                logger.LogDebug(exLink, "Failed to detect links for MyAnonamouse item {Id}", id);
            }
        }
    }
}
