/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    public partial class FileMover
    {
        public Task<bool> MoveFilePreservingPhysicalIdentityAsync(
            string source,
            string destination,
            string expectedSourcePhysicalObjectIdentity,
            Guid operationId) =>
            MoveFilePreservingPhysicalIdentityCoreAsync(
                source,
                destination,
                expectedSourcePhysicalObjectIdentity,
                operationId,
                audiobookId: null,
                audiobookFileId: null);

        public Task<bool> MoveFilePreservingPhysicalIdentityAsync(
            string source,
            string destination,
            string expectedSourcePhysicalObjectIdentity,
            Guid operationId,
            int audiobookId,
            int audiobookFileId)
        {
            if (audiobookId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(audiobookId));
            }
            if (audiobookFileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(audiobookFileId));
            }

            return MoveFilePreservingPhysicalIdentityCoreAsync(
                source,
                destination,
                expectedSourcePhysicalObjectIdentity,
                operationId,
                audiobookId,
                audiobookFileId);
        }

        private async Task<bool> MoveFilePreservingPhysicalIdentityCoreAsync(
            string source,
            string destination,
            string expectedSourcePhysicalObjectIdentity,
            Guid operationId,
            int? audiobookId,
            int? audiobookFileId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                expectedSourcePhysicalObjectIdentity);
            if (operationId == Guid.Empty)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    source,
                    destination,
                    "A durable generation-preserving move requires a non-empty operation ID");
                return false;
            }
            if (string.Equals(
                    Path.GetFullPath(source),
                    Path.GetFullPath(destination),
                    StringComparison.Ordinal))
            {
                try
                {
                    using var lease = PinnedAudiobookFileRegistrationLease.Open(
                        source,
                        expectedSourcePhysicalObjectIdentity);
                    return lease.MatchesCurrentPublication();
                }
                catch (Exception exception) when (exception is not (
                    OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    _logger.LogWarning(
                        exception,
                        "Blocked generation-preserving file move because the source identity is unavailable: {Source}",
                        LogRedaction.SanitizeFilePath(source));
                    return false;
                }
            }
            if (await IsNewMutationBlockedByCapabilityAsync(
                    FileAction.Move,
                    source,
                    destination,
                    operationId))
            {
                return false;
            }

            var markerlessResult =
                await TryMoveFilePreservingPhysicalIdentityMarkerlessAsync(
                    source,
                    destination,
                    expectedSourcePhysicalObjectIdentity,
                    operationId,
                    audiobookId,
                    audiobookFileId);
            if (markerlessResult.HasValue)
            {
                return markerlessResult.Value;
            }

            LogMutation(
                FileMutationOutcome.Blocked,
                FileAction.Move,
                source,
                destination,
                "Durable markerless file-move state is unavailable");
            return false;
        }

        internal async Task<bool> MoveFileAsync(
            string sourceFile,
            string destFile,
            Guid operationId,
            int? audiobookId = null,
            int? audiobookFileId = null,
            FilePublicationSourceProof? expectedSourceProof = null)
        {
            if (operationId == Guid.Empty)
            {
                LogMutation(
                    FileMutationOutcome.Blocked,
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    "A durable file move requires a non-empty operation ID");
                return false;
            }
            if (string.Equals(
                    Path.GetFullPath(sourceFile),
                    Path.GetFullPath(destFile),
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (await IsNewMutationBlockedByCapabilityAsync(
                    FileAction.Move,
                    sourceFile,
                    destFile,
                    operationId))
            {
                return false;
            }

            var markerlessResult = await TryMoveFileMarkerlessAsync(
                sourceFile,
                destFile,
                operationId,
                audiobookId,
                audiobookFileId,
                expectedSourceProof);
            if (markerlessResult.HasValue)
            {
                return markerlessResult.Value;
            }

            LogMutation(
                FileMutationOutcome.Blocked,
                FileAction.Move,
                sourceFile,
                destFile,
                "Durable markerless file-move state is unavailable");
            return false;
        }

    }
}
