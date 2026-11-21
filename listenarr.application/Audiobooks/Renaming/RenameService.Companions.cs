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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private const string CompanionOperationScope = "organize-companion";

    /// <summary>
    /// Carries the non-audio companions (cover.jpg, metadata.json, ...) left in the previous
    /// folder after a folder-changing organize, published the way import publishes companions:
    /// a live source proof, an operation id derived from it, a publication plan, a verified
    /// companion publication, and source retirement only once the destination is proven.
    /// Runs only when every tracked file left the previous folder and that folder held this
    /// book alone: no other book references it in the database, no file that is or may be audio
    /// is left in it or below it, it is not a library root and not a parent of the new folder. Hidden
    /// and blacklisted files stay, an existing destination is never overwritten, and nothing is
    /// deleted: the previous folder carries no ownership proof, so it is left in place.
    /// A companion that cannot be published stays where it is; the organize result is unchanged.
    /// </summary>
    private async Task MoveCompanionFilesAfterRenameAsync(
        Audiobook audiobook,
        string? previousFolder,
        string? newFolder,
        ApplicationSettings settings,
        IReadOnlyCollection<string> allowedRoots,
        FileSystemPathSemantics semantics,
        RenameResult result,
        CancellationToken cancellationToken)
    {
        if (result.RenamedFiles.Any(file => !file.Success || file.SourceRetained))
        {
            return;
        }

        var previous = NormalizePath(previousFolder);
        var next = NormalizePath(newFolder);
        if (string.IsNullOrWhiteSpace(previous)
            || string.IsNullOrWhiteSpace(next)
            || PathsEqual(previous, next, semantics)
            || allowedRoots.Any(root => PathsEqual(previous, root, semantics))
            || IsSamePathOrWithin(next, previous, semantics)
            || !_fileSystem.DirectoryExists(previous)
            || !_fileSystem.DirectoryExists(next))
        {
            // A library root, or a folder the book moved down into (a flat author folder), holds
            // files that belong to other books or to the folder itself, not to this book.
            return;
        }

        bool exclusive;
        try
        {
            exclusive = await AudiobookExclusiveFolderReference.IsExclusiveAsync(
                audiobook.Id,
                previous,
                semantics,
                _audiobookRepository,
                _audiobookFileRepository,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            // The organize already committed: a failed lookup leaves the companions in place
            // rather than turning that result into a failure.
            _logger.LogWarning(ex, "Could not check which books reference {Folder}; leaving its companion files in place", previous);
            return;
        }

        if (!exclusive)
        {
            // Another book's base path or tracked files sit in, below or above that folder (or
            // cannot be resolved), so its cover and metadata may be that book's.
            return;
        }

        List<string> companions;
        try
        {
            if (_fileSystem
                .EnumerateFiles(previous, "*", SearchOption.AllDirectories)
                .Any(FileUtils.MayBeAudioPendingProbe))
            {
                // Audio, or a container that may carry audio (.mp4), is still there or below it
                // and this book no longer tracks it: the folder is shared with another book,
                // tracked or not, whose cover and metadata must stay with it.
                return;
            }

            companions = _fileSystem
                .EnumerateFiles(previous, "*", SearchOption.TopDirectoryOnly)
                .Where(file => !Path.GetFileName(file).StartsWith('.'))
                .Where(file => !FileUtils.IsBlacklistedFile(file, settings.ImportBlacklistExtensions))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list companion files left in {Folder} after organize", previous);
            return;
        }

        var compatibilityBatchId = Guid.NewGuid();
        foreach (var companion in companions)
        {
            var destination = Path.Combine(next, Path.GetFileName(companion));
            if (!IsPathWithinAllowedRoots(companion, allowedRoots, semantics)
                || !IsPathWithinAllowedRoots(destination, allowedRoots, semantics)
                || !_fileSystem.TryValidateMutationTarget(companion, allowedRoots, out var source, out _)
                || !_fileSystem.TryValidateMutationTarget(destination, allowedRoots, out var target, out _))
            {
                _logger.LogWarning("Skipping companion file {FilePath}: outside the allowed library roots", companion);
                continue;
            }

            if (_fileSystem.FileExists(target))
            {
                _logger.LogInformation("Keeping companion file {FilePath} in place: {Destination} already exists", source, target);
                continue;
            }

            try
            {
                if (!await PublishCompanionAsync(
                        audiobook.Id,
                        source,
                        target,
                        semantics,
                        compatibilityBatchId,
                        cancellationToken))
                {
                    _logger.LogWarning("Companion file {FilePath} was not moved to {Destination}", source, target);
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(ex, "Failed to move companion file {FilePath} to {Destination}", source, target);
            }
        }
    }

    private async Task<bool> PublishCompanionAsync(
        int audiobookId,
        string source,
        string destination,
        FileSystemPathSemantics semantics,
        Guid compatibilityBatchId,
        CancellationToken cancellationToken)
    {
        var capability = await _filePublicationSourceCapability.CheckAsync(source, cancellationToken);
        if (!capability.IsSupported || !capability.SourceProof.HasValue)
        {
            _logger.LogWarning(
                "Keeping companion file {FilePath} in place: its source cannot be verified ({Reason})",
                source,
                capability.Reason);
            return false;
        }

        var sourceProof = capability.SourceProof.Value;
        var operationId = FileMoveOperationIdentity.CreateForPaths(
            CompanionOperationScope,
            audiobookId,
            FileAction.Move,
            source,
            semantics,
            sourceProof,
            destination,
            semantics);
        var plan = _filePublicationCapabilityResolver == null
            ? FilePublicationPlan.Durable(FileAction.Move)
            : await _filePublicationCapabilityResolver.ResolveAsync(
                FileAction.Move,
                source,
                destination,
                sourceProof,
                cancellationToken,
                compatibilityBatchId,
                CompatibilityCleanupOwner.Listenarr);
        if (!plan.IsAllowed)
        {
            _logger.LogWarning(
                "Keeping companion file {FilePath} in place: {Reason}",
                source,
                plan.Message);
            return false;
        }

        // The destination is the book's new folder, which the verified audio move has already
        // created, so no hierarchy is created here.
        var preparation = await _fileMover.PrepareActionForRegistrationDetailedAsync(
            plan,
            source,
            destination,
            operationId,
            expectedRegisteredPhysicalObjectIdentity: null,
            sourceProof,
            isCompanionFile: true,
            companionAudiobookId: audiobookId);
        using var lease = preparation.RegistrationLease;
        if (lease == null || !lease.PrepareCleanupRecovery(audiobookId))
        {
            return false;
        }

        if (lease.CompletePublication() == RegistrationPublicationCompletion.CommittedCleanupPending)
        {
            _logger.LogWarning(
                "Organize companion publication committed, but cleanup remains pending for {Path}",
                destination);
        }

        return plan.EffectiveAction != FileAction.Move
            || await _fileMover.CompletePreparedMoveAsync(source, destination, lease, operationId);
    }
}
