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

namespace Listenarr.Infrastructure.Library.Moving
{
    internal partial class MoveJobProcessor
    {
        private static bool HasFinalizedMoveEvidence(
            MoveJob job,
            Audiobook audiobook,
            string target,
            FileSystemPathSemantics targetSemantics)
        {
            if (job.Phase >= MoveJobPhase.Published)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                return false;
            }

            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    audiobook.BasePath,
                    out var currentBasePath,
                    out _))
            {
                return false;
            }

            try
            {
                return FileSystemPathIdentity.AreEquivalent(
                    currentBasePath,
                    target,
                    targetSemantics);
            }
            catch (Exception exception) when (exception is
                ArgumentException or NotSupportedException
                    or PathTooLongException
                    or System.Security.SecurityException)
            {
                return false;
            }
        }

        private sealed record FinalizedMoveRecoveryOutcome(
            bool Handled,
            AudiobookContentMoveResult? MoveResult)
        {
            public static FinalizedMoveRecoveryOutcome NotAttempted { get; } =
                new(false, null);
            public static FinalizedMoveRecoveryOutcome HandledFailure { get; } =
                new(true, null);
        }
    }
}
