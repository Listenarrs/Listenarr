/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Renaming
{
    public partial class RenameService
    {
        private static string RequireStoredAbsolutePathForHost(
            string path,
            string error)
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    path,
                    out var canonicalPath,
                    out _))
            {
                throw new InvalidOperationException(error);
            }

            return canonicalPath;
        }

        private static string? TryResolveStoredAbsolutePathForHost(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out _)
                ? canonicalPath
                : null;
        }
    }
}
