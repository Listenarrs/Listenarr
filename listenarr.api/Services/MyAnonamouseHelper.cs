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

namespace Listenarr.Api.Services
{
    internal static class MyAnonamouseHelper
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
                // Swallow parsing errors; this helper is best-effort
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
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
                // Ignore malformed host
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
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

        // Replace occurrences of a host inside bencoded torrent content while preserving bencode string lengths.
        // This is a minimal, focused implementation that walks bencoded data and rewrites byte strings
        // that contain the oldHost by substituting the host name and updating the length prefix.
        public static byte[] ReplaceHostInTorrent(byte[] torrentBytes, string oldHost, string newHost)
        {
            using var inStream = new System.IO.MemoryStream(torrentBytes);
            using var outStream = new System.IO.MemoryStream();

            string ReadNumber()
            {
                var sb = new System.Text.StringBuilder();
                while (true)
                {
                    int b = inStream.ReadByte();
                    if (b == -1) break;
                    if (b == (int)':') break;
                    sb.Append((char)b);
                }
                return sb.ToString();
            }

            void CopyElement()
            {
                int c = inStream.ReadByte();
                if (c == -1) return;
                char ch = (char)c;
                if (ch == 'd' || ch == 'l')
                {
                    // dict or list
                    outStream.WriteByte((byte)c);
                    while (true)
                    {
                        int peek = inStream.ReadByte();
                        if (peek == -1) break;
                        if ((char)peek == 'e')
                        {
                            outStream.WriteByte((byte)peek);
                            break;
                        }
                        inStream.Position -= 1;
                        // For dicts, keys are strings; for lists, elements can be any
                        // Recurse
                        CopyElement();
                    }
                }
                else if (ch == 'i')
                {
                    // integer: read until 'e'
                    var sb = new System.Text.StringBuilder();
                    sb.Append('i');
                    while (true)
                    {
                        int b = inStream.ReadByte();
                        if (b == -1) break;
                        sb.Append((char)b);
                        if ((char)b == 'e') break;
                    }
                    var s = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
                    outStream.Write(s, 0, s.Length);
                }
                else if (char.IsDigit(ch))
                {
                    // byte string: read length up to ':'
                    inStream.Position -= 1;
                    var lenStr = ReadNumber();
                    var len = int.Parse(lenStr);
                    // read ':' consumed by ReadNumber
                    // read the data
                    var data = new byte[len];
                    var read = inStream.Read(data, 0, len);

                    var dataStr = System.Text.Encoding.UTF8.GetString(data, 0, read);
                    if (dataStr.Contains(oldHost, StringComparison.OrdinalIgnoreCase))
                    {
                        var replaced = dataStr.Replace(oldHost, newHost, StringComparison.OrdinalIgnoreCase);
                        var replacedBytes = System.Text.Encoding.UTF8.GetBytes(replaced);
                        var newLenStr = replacedBytes.Length.ToString();
                        var prefix = System.Text.Encoding.ASCII.GetBytes(newLenStr + ":");
                        outStream.Write(prefix, 0, prefix.Length);
                        outStream.Write(replacedBytes, 0, replacedBytes.Length);
                    }
                    else
                    {
                        var prefix = System.Text.Encoding.ASCII.GetBytes(lenStr + ":");
                        outStream.Write(prefix, 0, prefix.Length);
                        outStream.Write(data, 0, read);
                    }
                }
                else
                {
                    // unknown - write the byte and continue
                    outStream.WriteByte((byte)c);
                }
            }

            // Walk the top-level element(s)
            while (inStream.Position < inStream.Length)
            {
                CopyElement();
            }

            return outStream.ToArray();
        }

        // Replace an exact byte-string value inside bencoded torrent content (preserves bencode length prefixes)
        // Only replaces when the byte string matches `oldValue` exactly; useful for rewriting announce URLs safely.
        public static byte[] ReplaceStringInTorrent(byte[] torrentBytes, string oldValue, string newValue)
        {
            using var inStream = new System.IO.MemoryStream(torrentBytes);
            using var outStream = new System.IO.MemoryStream();

            string ReadNumberLocal()
            {
                var sb = new System.Text.StringBuilder();
                while (true)
                {
                    int b = inStream.ReadByte();
                    if (b == -1) break;
                    if (b == (int)':') break;
                    sb.Append((char)b);
                }
                return sb.ToString();
            }

            void CopyElement()
            {
                int c = inStream.ReadByte();
                if (c == -1) return;
                char ch = (char)c;
                if (ch == 'd' || ch == 'l')
                {
                    outStream.WriteByte((byte)c);
                    while (true)
                    {
                        int peek = inStream.ReadByte();
                        if (peek == -1) break;
                        if ((char)peek == 'e')
                        {
                            outStream.WriteByte((byte)peek);
                            break;
                        }
                        inStream.Position -= 1;
                        CopyElement();
                    }
                }
                else if (ch == 'i')
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append('i');
                    while (true)
                    {
                        int b = inStream.ReadByte();
                        if (b == -1) break;
                        sb.Append((char)b);
                        if ((char)b == 'e') break;
                    }
                    var s = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
                    outStream.Write(s, 0, s.Length);
                }
                else if (char.IsDigit(ch))
                {
                    inStream.Position -= 1;
                    var lenStr = ReadNumberLocal();
                    if (!int.TryParse(lenStr, out var len)) return;
                    // read ':' consumed by ReadNumberLocal
                    var data = new byte[len];
                    var read = inStream.Read(data, 0, len);
                    var dataStr = System.Text.Encoding.UTF8.GetString(data, 0, read);
                    if (string.Equals(dataStr, oldValue, StringComparison.Ordinal))
                    {
                        var replacedBytes = System.Text.Encoding.UTF8.GetBytes(newValue);
                        var newLenStr = replacedBytes.Length.ToString();
                        var prefix = System.Text.Encoding.ASCII.GetBytes(newLenStr + ":");
                        outStream.Write(prefix, 0, prefix.Length);
                        outStream.Write(replacedBytes, 0, replacedBytes.Length);
                    }
                    else
                    {
                        var prefix = System.Text.Encoding.ASCII.GetBytes(lenStr + ":");
                        outStream.Write(prefix, 0, prefix.Length);
                        outStream.Write(data, 0, read);
                    }
                }
                else
                {
                    outStream.WriteByte((byte)c);
                }
            }

            while (inStream.Position < inStream.Length)
            {
                CopyElement();
            }

            return outStream.ToArray();
        }

        // Extract announce/trackers from bencoded torrent content.
        // Returns a list of strings including http(s) and udp trackers and any explicit announce-list entries.
        public static List<string> ExtractAnnounceUrls(byte[] torrentBytes)
        {
            var resultSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var inStream = new System.IO.MemoryStream(torrentBytes);

                string ReadNumberLocal()
                {
                    var sb = new System.Text.StringBuilder();
                    while (true)
                    {
                        int b = inStream.ReadByte();
                        if (b == -1) break;
                        if (b == (int)':') break;
                        sb.Append((char)b);
                    }
                    return sb.ToString();
                }

                string ReadStringLocal(int len)
                {
                    var buf = new byte[len];
                    var r = inStream.Read(buf, 0, len);
                    return System.Text.Encoding.UTF8.GetString(buf, 0, r);
                }

                // Skip over a bencoded element without capturing any strings
                void ScanElementSkip()
                {
                    int c2 = inStream.ReadByte();
                    if (c2 == -1) return;
                    char ch2 = (char)c2;
                    if (ch2 == 'd')
                    {
                        while (true)
                        {
                            int p = inStream.ReadByte();
                            if (p == -1 || (char)p == 'e') break;
                            inStream.Position -= 1;
                            // skip key (string)
                            var kl = ReadNumberLocal();
                            if (!int.TryParse(kl, out var kLen)) break;
                            ReadStringLocal(kLen);
                            ScanElementSkip(); // skip value
                        }
                    }
                    else if (ch2 == 'l')
                    {
                        while (true)
                        {
                            int p = inStream.ReadByte();
                            if (p == -1 || (char)p == 'e') break;
                            inStream.Position -= 1;
                            ScanElementSkip();
                        }
                    }
                    else if (ch2 == 'i')
                    {
                        while (true)
                        {
                            int b = inStream.ReadByte();
                            if (b == -1 || (char)b == 'e') break;
                        }
                    }
                    else if (char.IsDigit(ch2))
                    {
                        inStream.Position -= 1;
                        var ls = ReadNumberLocal();
                        if (int.TryParse(ls, out var len)) ReadStringLocal(len);
                    }
                }

                void ScanElement()
                {
                    int c = inStream.ReadByte();
                    if (c == -1) return;
                    char ch = (char)c;
                    if (ch == 'd')
                    {
                        // dict: read key/value pairs until 'e'
                        while (true)
                        {
                            int peek = inStream.ReadByte();
                            if (peek == -1) break;
                            if ((char)peek == 'e') break; // 'e' is consumed here; no extra read needed
                            inStream.Position -= 1;
                            // keys are strings
                            var keyLenStr = ReadNumberLocal();
                            if (!int.TryParse(keyLenStr, out var keyLen)) break;
                            var key = ReadStringLocal(keyLen);

                            // Value can be any bencoded type - if key is announce or announce-list/url-list, capture appropriate strings
                            if (string.Equals(key, "announce", StringComparison.OrdinalIgnoreCase))
                            {
                                // next is string
                                var lenStr = ReadNumberLocal();
                                if (!int.TryParse(lenStr, out var len)) continue;
                                var val = ReadStringLocal(len);
                                if (!string.IsNullOrWhiteSpace(val)) resultSet.Add(val);
                            }
                            else if (string.Equals(key, "announce-list", StringComparison.OrdinalIgnoreCase))
                            {
                                // value is a list (possibly nested) of tracker announce URLs
                                ScanElement(); // will process nested lists/strings and add strings when encountered
                            }
                            else if (string.Equals(key, "url-list", StringComparison.OrdinalIgnoreCase))
                            {
                                // url-list is for web seeds / file URLs — NOT tracker announces.
                                // Skip by scanning without capturing.
                                ScanElementSkip();
                            }
                            else
                            {
                                // For other keys, scan the value recursively
                                ScanElement();
                            }
                        }
                    }
                    else if (ch == 'l')
                    {
                        // list: elements until 'e'
                        while (true)
                        {
                            int peek = inStream.ReadByte();
                            if (peek == -1) break;
                            if ((char)peek == 'e') break; // 'e' is consumed here; no extra read needed
                            inStream.Position -= 1;
                            // If element is a string, capture it; otherwise recurse
                            int next = inStream.ReadByte();
                            if (next == -1) break;
                            char nCh = (char)next;
                            if (char.IsDigit(nCh))
                            {
                                inStream.Position -= 1;
                                var lenStr = ReadNumberLocal();
                                if (!int.TryParse(lenStr, out var len)) break;
                                var s = ReadStringLocal(len);
                                if (!string.IsNullOrWhiteSpace(s)) resultSet.Add(s);
                            }
                            else
                            {
                                inStream.Position -= 1;
                                ScanElement();
                            }
                        }
                    }
                    else if (ch == 'i')
                    {
                        // integer: read until 'e'
                        while (true)
                        {
                            int b = inStream.ReadByte();
                            if (b == -1) break;
                            if ((char)b == 'e') break;
                        }
                    }
                    else if (char.IsDigit(ch))
                    {
                        // byte string: read length and string; if the string looks like a URL (http/https/udp) add it
                        inStream.Position -= 1;
                        var lenStr = ReadNumberLocal();
                        if (!int.TryParse(lenStr, out var len)) return;
                        // ReadNumberLocal already consumed the ':' separator, so read the string directly
                        var s = ReadStringLocal(len);
                        if (!string.IsNullOrWhiteSpace(s) && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)))
                        {
                            resultSet.Add(s);
                        }
                    }
                    else
                    {
                        // unknown - nothing to do
                    }
                }

                // Start scanning from the beginning
                inStream.Position = 0;
                ScanElement();
            }
            catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException)
            {
                // best-effort, swallow errors
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            // Fallback: regex to find tracker announce URLs if bencode parsing found nothing.
            // Only match URLs containing /announce or /tracker to avoid picking up file/web-seed URLs.
            if (resultSet.Count == 0)
            {
                try
                {
                    var asciiAll = System.Text.Encoding.ASCII.GetString(torrentBytes);
                    var matches = System.Text.RegularExpressions.Regex.Matches(asciiAll, @"(https?|udp)://[^\s\""']+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    foreach (var v in matches.Select(m => m.Value).Where(v => v.Contains("/announce", StringComparison.OrdinalIgnoreCase) || v.Contains("/tracker", StringComparison.OrdinalIgnoreCase)))
                    {
                        resultSet.Add(v);
                    }
                }
                catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException)
                {
                    // ignore
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            return new List<string>(resultSet);
        }
    }
}
