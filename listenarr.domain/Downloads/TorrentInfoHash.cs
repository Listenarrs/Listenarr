/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Domain.Downloads
{
    /// <summary>
    /// Puts a torrent info-hash into one spelling. A magnet link may carry the 20-byte hash as 40
    /// hexadecimal characters or as 32 base32 characters (BEP 9), and the two spell the same
    /// torrent. The blocklist key is built from the result, so a release blocked under one spelling
    /// is still blocked when an indexer lists it under the other.
    /// </summary>
    public static class TorrentInfoHash
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        private const int HexLength = 40;
        private const int Base32Length = 32;

        /// <summary>
        /// Lower-case hexadecimal for a 40-character hex or 32-character base32 hash; any other
        /// value is trimmed and lower-cased unchanged, so a malformed hash still keys on what the
        /// indexer sent.
        /// </summary>
        public static string Normalize(string torrentInfoHash)
        {
            var trimmed = torrentInfoHash.Trim();
            return ToHex(trimmed) ?? trimmed.ToLowerInvariant();
        }

        /// <summary>
        /// The info-hash as lower-case hexadecimal, or null when the value is neither 40 hex
        /// characters nor 32 base32 characters.
        /// </summary>
        public static string? ToHex(string value)
        {
            if (value.Length == HexLength && value.All(Uri.IsHexDigit))
            {
                return value.ToLowerInvariant();
            }

            return value.Length == Base32Length && TryDecodeBase32(value, out var bytes)
                ? Convert.ToHexString(bytes).ToLowerInvariant()
                : null;
        }

        private static bool TryDecodeBase32(string value, out byte[] decoded)
        {
            decoded = new byte[HexLength / 2];
            var outputIndex = 0;
            var buffer = 0;
            var bits = 0;

            foreach (var character in value.ToUpperInvariant())
            {
                var digit = Base32Alphabet.IndexOf(character);
                if (digit < 0)
                {
                    return false;
                }

                buffer = (buffer << 5) | digit;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    decoded[outputIndex++] = (byte)(buffer >> bits);
                    buffer &= (1 << bits) - 1;
                }
            }

            return outputIndex == decoded.Length;
        }
    }
}
