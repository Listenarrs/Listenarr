/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Infrastructure.Downloads.DirectDownload;

internal static class DirectDownloadArtifactFileNames
{
    public static bool TryNormalizeArtifactFileName(
        string? value,
        out string fileName,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            fileName = string.Empty;
            error = "The direct-download artifact filename is invalid.";
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.IndexOfAny(['/', '\\']) >= 0 || trimmed is "." or "..")
        {
            fileName = string.Empty;
            error = "The direct-download artifact filename is invalid.";
            return false;
        }

        fileName = SanitizePathSegment(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            error = "The direct-download artifact filename is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            fileName += ".download";
        }

        error = string.Empty;
        return true;
    }

    public static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character =>
            invalidChars.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }
}
