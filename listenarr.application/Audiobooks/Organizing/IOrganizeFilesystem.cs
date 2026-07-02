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

namespace Listenarr.Application.Audiobooks.Organizing
{
    /// <summary>
    /// Why a flatten would (or wouldn't) succeed.
    /// <see cref="FlattenFeasibility.Ok"/> is the only value
    /// <see cref="IOrganizeFilesystem.ExecuteFlatten"/> proceeds on.
    /// </summary>
    public enum FlattenFeasibility
    {
        Ok,
        SourcePathEmpty,
        TargetPathEmpty,
        SourceMissing,
        SamePath,
        NotAncestor,
        TargetMissing,
        NoUsableParent,
        TargetHasForeignFiles,
        NoFiles,
    }

    public sealed class FlattenOutcome
    {
        public bool Success { get; init; }
        public int FilesMoved { get; init; }
        public string? ErrorMessage { get; init; }
        public string? TempPathUsed { get; init; }
    }

    /// <summary>
    /// Filesystem primitives for the organize-library flow: metadata-stub
    /// detection, populated-target checks, and the strongly-guarded flatten
    /// used for "nested one level too deep" rows. Implemented in the
    /// infrastructure layer (the only layer allowed to touch the filesystem);
    /// consumed by the organize preview/apply workflows and the move-job
    /// processor's execute-time guards.
    /// </summary>
    public interface IOrganizeFilesystem
    {
        /// <summary>
        /// True when <paramref name="path"/> is an existing directory that
        /// contains no audio files at any depth — a metadata-only husk (covers,
        /// .opf, playlists) left behind by a removed release. On any read error
        /// it is NOT treated as a stub.
        /// </summary>
        bool IsMetadataStubDirectory(string path);

        /// <summary>
        /// True when the target directory exists on disk and is not empty.
        /// </summary>
        bool TargetExistsWithContent(string target);

        /// <summary>
        /// Read-only feasibility check for <see cref="ExecuteFlatten"/>: decides
        /// whether collapsing <paramref name="source"/> into ancestor
        /// <paramref name="dest"/> is safe, and returns the file count, without
        /// mutating the filesystem. Shared by the organize preview (to bucket a
        /// nested row and decide whether to offer the one-click flatten) and by
        /// <see cref="ExecuteFlatten"/> so the preview's promise and the
        /// executor's guard can never disagree.
        /// </summary>
        (FlattenFeasibility Feasibility, int FileCount) EvaluateFlatten(string source, string dest);

        /// <summary>Human-readable explanation of a non-Ok <see cref="FlattenFeasibility"/>.</summary>
        string DescribeFeasibility(FlattenFeasibility feasibility);

        /// <summary>
        /// Flatten a redundantly-nested folder: collapse everything under
        /// <paramref name="source"/> up into strict ancestor <paramref name="dest"/>.
        /// Strongly guarded via <see cref="EvaluateFlatten"/> — refuses unless
        /// every file under dest already lives under source, so only empty
        /// wrapper directories are ever collapsed.
        /// </summary>
        FlattenOutcome ExecuteFlatten(string source, string dest, Guid jobId);
    }
}
