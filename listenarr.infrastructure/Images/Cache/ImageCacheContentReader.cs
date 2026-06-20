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

namespace Listenarr.Infrastructure.Images.Cache
{
    internal static class ImageCacheContentReader
    {
        public static async Task<byte[]> ReadWithLimitAsync(HttpContent content, long maxBytes)
        {
            await using var contentStream = await content.ReadAsStreamAsync();
            using var bufferStream = new MemoryStream();
            var buffer = new byte[81920];
            long totalBytes = 0;

            while (true)
            {
                var read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > maxBytes)
                {
                    throw new InvalidOperationException($"Downloaded image exceeds the {maxBytes} byte limit.");
                }

                bufferStream.Write(buffer, 0, read);
            }

            return bufferStream.ToArray();
        }
    }
}
