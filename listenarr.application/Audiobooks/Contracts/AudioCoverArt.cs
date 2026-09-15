/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Audiobooks.Contracts
{
    /// <summary>
    /// Cover artwork to embed into an audio file, with the MIME type the container needs
    /// to record alongside the bytes.
    /// </summary>
    public sealed record AudioCoverArt(byte[] Data, string MimeType)
    {
        /// <summary>
        /// Identify the image from its leading bytes rather than trusting a file extension
        /// or a Content-Type header, neither of which is available by the time the bytes
        /// reach the tag writer. Returns null when the bytes are not an image this can
        /// name, because writing artwork under the wrong MIME type produces a file players
        /// silently refuse to show a cover for.
        /// </summary>
        public static AudioCoverArt? FromBytes(byte[]? data)
        {
            if (data is null || data.Length < 12)
            {
                return null;
            }

            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                return new AudioCoverArt(data, "image/jpeg");
            }

            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                return new AudioCoverArt(data, "image/png");
            }

            // "RIFF" .... "WEBP"
            if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            {
                return new AudioCoverArt(data, "image/webp");
            }

            return null;
        }
    }
}
