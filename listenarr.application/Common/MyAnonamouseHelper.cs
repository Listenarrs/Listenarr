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
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Net;
using System.Text.Json;

namespace Listenarr.Application.Common
{
    public static class MyAnonamouseHelper
    {
        private const string DefaultBaseUrl = "https://www.myanonamouse.net";
        private static readonly string[] MamKeys = { "mam_id", "mamid", "mamId", "mamID", "mam" };

        public static string? TryGetMamId(string? additionalSettings)
        {
            if (string.IsNullOrWhiteSpace(additionalSettings))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(additionalSettings);
                return FindMamId(doc.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static HttpClient CreateAuthenticatedHttpClient(string mamId, string? baseUrl, TimeSpan? timeout = null)
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = BuildCookieContainer(mamId, baseUrl),
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
                // Disable automatic redirects so we can re-apply cookies/host header across locations (matches Prowlarr behavior)
                AllowAutoRedirect = false
            };

            var client = new HttpClient(handler);
            client.Timeout = timeout ?? TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            client.DefaultRequestHeaders.Referrer = new Uri(DefaultBaseUrl);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            return client;
        }

        public static string? TryExtractMamIdFromResponse(HttpResponseMessage response)
        {
            try
            {
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
                {
                    foreach (var sc in setCookieValues)
                    {
                        // Look for mam_id=VALUE in the header value
                        var m = System.Text.RegularExpressions.Regex.Match(sc, @"\bmam_id=([^;\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success)
                            return m.Groups[1].Value.Trim('"');
                    }
                }
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                // Nothing is logged here: MyAnonamouseHelper is a static helper with no logger; the caller receives null and treats the cookie as absent.
            }

            return null;
        }

        public static string UpdateMamIdInAdditionalSettings(string? additionalSettings, string mamId)
        {
            if (string.IsNullOrWhiteSpace(additionalSettings))
            {
                var obj = new System.Text.Json.Nodes.JsonObject();
                obj["mam_id"] = mamId;
                return obj.ToJsonString();
            }

            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(additionalSettings) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                node["mam_id"] = mamId;
                return node.ToJsonString();
            }
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
            {
                var obj = new System.Text.Json.Nodes.JsonObject();
                obj["mam_id"] = mamId;
                return obj.ToJsonString();
            }
        }

        public static CookieContainer BuildCookieContainer(string mamId, string? baseUrl)
        {
            var container = new CookieContainer();
            var baseUri = NormalizeBaseUri(baseUrl);
            container.Add(baseUri, new Cookie("mam_id", mamId));

            try
            {
                var host = baseUri.Host;
                if (!host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    var wwwUri = new Uri($"{baseUri.Scheme}://www.{host}");
                    container.Add(wwwUri, new Cookie("mam_id", mamId));
                }
            }
            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
            {
                // Nothing is logged here: MyAnonamouseHelper is a static helper with no logger; a malformed host simply gets no www alias cookie.
            }

            return container;
        }

        public static string ResolveTorrentFileName(HttpResponseMessage response, string torrentUrl)
        {
            var contentDisposition = response.Content.Headers.ContentDisposition;
            if (contentDisposition != null)
            {
                if (!string.IsNullOrWhiteSpace(contentDisposition.FileNameStar))
                    return TrimFileName(contentDisposition.FileNameStar);
                if (!string.IsNullOrWhiteSpace(contentDisposition.FileName))
                    return TrimFileName(contentDisposition.FileName);
            }

            if (Uri.TryCreate(torrentUrl, UriKind.Absolute, out var uri))
            {
                var name = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }

            return "myanonamouse.torrent";
        }

        private static string TrimFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return fileName;

            return fileName.Trim().Trim('"');
        }

        private static Uri NormalizeBaseUri(string? baseUrl)
        {
            var trimmed = baseUrl?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(trimmed))
                trimmed = DefaultBaseUrl;

            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }

            return new Uri(trimmed);
        }

        private static string? FindMamId(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (MamKeys.Any(k => string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase)) &&
                        prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();

                    if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var nested = FindMamId(prop.Value);
                        if (!string.IsNullOrEmpty(nested))
                            return nested;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var nested in element.EnumerateArray().Select(FindMamId).Where(n => !string.IsNullOrEmpty(n)))
                {
                    return nested;
                }
            }

            return null;
        }

        public static byte[] ReplaceHostInTorrent(byte[] torrentBytes, string oldHost, string newHost)
        {
            return MyAnonamouseTorrentBencodeHelper.ReplaceHostInTorrent(torrentBytes, oldHost, newHost);
        }

        public static byte[] ReplaceStringInTorrent(byte[] torrentBytes, string oldValue, string newValue)
        {
            return MyAnonamouseTorrentBencodeHelper.ReplaceStringInTorrent(torrentBytes, oldValue, newValue);
        }

        public static List<string> ExtractAnnounceUrls(byte[] torrentBytes)
        {
            return MyAnonamouseTorrentBencodeHelper.ExtractAnnounceUrls(torrentBytes);
        }

        /// <summary>
        /// Normalize mam_id by decoding any existing encoding and then encoding exactly once
        /// </summary>
        public static string NormalizeMamId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var decoded = raw;
            while (true)
            {
                var next = Uri.UnescapeDataString(decoded);
                if (next == decoded) break;
                decoded = next;
            }
            return Uri.EscapeDataString(decoded);
        }
    }
}
