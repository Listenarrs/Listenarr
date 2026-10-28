/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
namespace Listenarr.Api.Features.Library;

internal static class RootFolderRequestValidation
{
    public static bool TryParseRelocationMode(
        string? value,
        out RootFolderRelocationMode mode)
    {
        if (string.Equals(value, "relocate", StringComparison.OrdinalIgnoreCase))
        {
            mode = RootFolderRelocationMode.Relocate;
            return true;
        }

        if (string.Equals(value, "metadataOnly", StringComparison.OrdinalIgnoreCase))
        {
            mode = RootFolderRelocationMode.MetadataOnly;
            return true;
        }

        mode = default;
        return false;
    }
}
