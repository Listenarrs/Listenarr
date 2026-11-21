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
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

/// <summary>
/// Decides from the database whether a folder belongs to one audiobook alone before its
/// companion files are carried elsewhere: no other audiobook may have its base path at, inside
/// or above the folder, nor a legacy or tracked file inside it. A reference that cannot be
/// resolved counts as a conflict.
/// </summary>
public static class AudiobookExclusiveFolderReference
{
    public static async Task<bool> IsExclusiveAsync(
        int audiobookId,
        string folder,
        FileSystemPathSemantics semantics,
        IAudiobookRepository audiobookRepository,
        IAudiobookFileRepository fileRepository,
        CancellationToken cancellationToken)
    {
        var otherAudiobooks = (await audiobookRepository
                .GetOtherPathReferenceSnapshotsAsync(audiobookId, cancellationToken))
            .ToDictionary(candidate => candidate.AudiobookId);
        foreach (var other in otherAudiobooks.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(other.BasePath))
            {
                if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        other.BasePath,
                        out var otherBasePath,
                        out _))
                {
                    return false;
                }

                if (FileSystemPathIdentity.IsSameOrInside(otherBasePath, folder, semantics)
                    || FileSystemPathIdentity.IsSameOrInside(folder, otherBasePath, semantics))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(other.FilePath))
            {
                if (!TryResolveStoredFilePath(other.BasePath, other.FilePath, semantics, out var legacyPath))
                {
                    return false;
                }

                if (FileSystemPathIdentity.IsSameOrInside(legacyPath, folder, semantics))
                {
                    return false;
                }
            }
        }

        foreach (var file in await fileRepository
            .GetOtherPathReferenceSnapshotsAsync(audiobookId, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!otherAudiobooks.TryGetValue(file.AudiobookId, out var owner)
                || !TryResolveStoredFilePath(owner.BasePath, file.Path!, semantics, out var otherFilePath))
            {
                return false;
            }

            if (FileSystemPathIdentity.IsSameOrInside(otherFilePath, folder, semantics))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveStoredFilePath(
        string? audiobookBasePath,
        string storedPath,
        FileSystemPathSemantics semantics,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(storedPath, out _))
        {
            return FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                storedPath,
                out resolvedPath,
                out _);
        }

        return !string.IsNullOrWhiteSpace(audiobookBasePath)
            && FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                audiobookBasePath,
                out var basePath,
                out _)
            && FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                basePath,
                storedPath,
                semantics,
                out resolvedPath);
    }
}
