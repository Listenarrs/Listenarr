/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Domain.Common;

public static partial class FileUtils
{
    public static IReadOnlyList<string> GetValidMutationRootsForCurrentOs(
        IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var normalizedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    path,
                    out var normalizedPath,
                    out _))
            {
                continue;
            }

            normalizedRoots.Add(normalizedPath);
        }

        return normalizedRoots.ToArray();
    }
}
