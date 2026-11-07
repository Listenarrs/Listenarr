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
namespace Listenarr.Application.Common
{
    public static class MyAnonamouseTorrentBencodeHelper
    {
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
                // Nothing is logged here: this bencode helper is static with no logger; the regex fallback below covers a failed parse.
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
                    // Nothing is logged here: this bencode helper is static with no logger; the caller receives whatever the parse found.
                }
            }

            return new List<string>(resultSet);
        }
    }
}
