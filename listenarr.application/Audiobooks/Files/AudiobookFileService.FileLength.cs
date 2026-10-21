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
 */
namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    /// <summary>
    /// Read the byte length of the file behind a registration metadata path.
    /// The pinned lease's metadata path is a /proc/self/fd magic link on
    /// Linux, and FileSystemInfo reads the link inode itself, whose reported
    /// size is a constant 64 — not the file's length. Opening the path
    /// follows the link to the pinned file, so the stream length is the real
    /// size of the exact object the lease holds open.
    /// </summary>
    private static long? TryGetRegisteredFileLength(string metadataPath)
    {
        try
        {
            using var stream = new FileStream(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
