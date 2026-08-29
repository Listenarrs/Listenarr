/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static void EnsureTargetCanReceiveContents(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        bool sourceInsideTarget,
        bool resumingOwnedDirectCopy,
        FileSystemPathSemantics semantics,
        LibraryDirectoryOwnership? targetDirectoryOwnership)
    {
        if (!Directory.Exists(target) || resumingOwnedDirectCopy)
        {
            return;
        }

        RevalidateTargetDirectoryOwnership(request, targetDirectoryOwnership);
        // When moving a child folder back into its parent, the target necessarily contains
        // the source subtree. That subtree is not a collision because it is the content being moved.
        var targetHasBlockingContent = Directory
            .EnumerateFileSystemEntries(target)
            .Any(entry => !(sourceInsideTarget
                && IsTargetEntryAllowedBySourceSubtree(entry, source, semantics)));
        if (targetHasBlockingContent)
        {
            throw new MoveNeedsAttentionException(sourceInsideTarget
                ? "Destination contains unrelated content outside the source subtree"
                : "Target directory already exists and contains files");
        }
    }

    private static bool IsTargetEntryAllowedBySourceSubtree(
        string entry,
        string source,
        FileSystemPathSemantics semantics)
    {
        if (IsSameOrInside(entry, source, semantics))
        {
            return true;
        }

        if (!Directory.Exists(entry) || !IsSameOrInside(source, entry, semantics))
        {
            return false;
        }

        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                entry,
                out var files,
                out var directories,
                out _))
        {
            return false;
        }

        return files
            .Concat(directories)
            .All(child => IsSameOrInside(child, source, semantics) || IsSameOrInside(source, child, semantics));
    }
}
