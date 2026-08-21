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

namespace Listenarr.Application.Downloads.Contracts
{
    public enum FilePublicationOutcome
    {
        Success,
        Skipped,
        Blocked,
        Failed
    }

    public sealed record FilePublicationPreparationResult(
        FilePublicationOutcome Outcome,
        FileAction RequestedAction,
        FileAction EffectiveAction,
        FilePublicationSourceDisposition SourceDisposition,
        IAudiobookFileRegistrationLease? RegistrationLease = null,
        string? ReasonCode = null,
        string? Message = null)
    {
        public bool IsSuccess =>
            Outcome == FilePublicationOutcome.Success
            && RegistrationLease != null;
    }

    /// <summary>
    /// Handles file manipulation within a destination hierarchy that has already
    /// been established by the caller. Implementations must not create missing
    /// managed destination parents; library hierarchy creation and enrollment
    /// belong to <see cref="ILibraryDirectoryOwnershipStore"/>.
    /// </summary>
    public interface IFileMover
    {
        Task<bool> MoveFilePreservingPhysicalIdentityAsync(
            string source,
            string destination,
            string expectedSourcePhysicalObjectIdentity,
            Guid operationId);

        Task<bool> MoveFilePreservingPhysicalIdentityAsync(
            string source,
            string destination,
            string expectedSourcePhysicalObjectIdentity,
            Guid operationId,
            int audiobookId,
            int audiobookFileId);

        /// <summary>
        /// Perform the given action on the given file
        /// </summary>
        /// <param name="action">What we want to do with the file</param>
        /// <param name="source">File</param>
        /// <param name="destination">Optional destination of the action</param>
        /// <param name="operationId">Stable identifier for a retryable filesystem operation</param>
        /// <returns>True in case of success, false otherwise</returns>
        Task<bool> PerformActionOn(
            FileAction action,
            string source,
            string? destination,
            Guid operationId);

        Task<bool> PerformActionOn(
            FileAction action,
            string source,
            string? destination,
            Guid operationId,
            FilePublicationSourceProof expectedSourceProof);

        Task<bool> PerformActionOn(
            FileAction action,
            string source,
            string? destination,
            Guid operationId,
            int audiobookId,
            int audiobookFileId);

        Task<bool> PerformActionOn(
            FileAction action,
            string source,
            string? destination,
            Guid operationId,
            int audiobookId,
            int audiobookFileId,
            FilePublicationSourceProof expectedSourceProof);

        /// <summary>
        /// Publishes the requested copy or hardlink destination and returns a lease
        /// bound to the exact published file generation. Move requests are staged as
        /// a copy; callers must retire the source only after durable registration.
        /// </summary>
        Task<IAudiobookFileRegistrationLease?> PrepareActionForRegistrationAsync(
            FileAction action,
            string source,
            string destination,
            Guid operationId);

        /// <summary>
        /// Resumes a registration publication when durable audiobook-file
        /// ownership already proves the expected destination generation.
        /// </summary>
        Task<IAudiobookFileRegistrationLease?> PrepareActionForRegistrationAsync(
            FileAction action,
            string source,
            string destination,
            Guid operationId,
            string expectedRegisteredPhysicalObjectIdentity);

        /// <summary>
        /// Publishes a registration candidate only when the source still matches
        /// the exact generation and content proof used to derive the durable operation ID.
        /// </summary>
        Task<IAudiobookFileRegistrationLease?> PrepareActionForRegistrationAsync(
            FileAction action,
            string source,
            string destination,
            Guid operationId,
            string? expectedRegisteredPhysicalObjectIdentity,
            FilePublicationSourceProof expectedSourceProof);

        /// <summary>
        /// Publishes through the explicitly selected durable or additive-only
        /// execution mode and reports the effective action and source disposition.
        /// </summary>
        Task<FilePublicationPreparationResult>
            PrepareActionForRegistrationDetailedAsync(
                FilePublicationPlan plan,
                string source,
                string destination,
                Guid operationId,
                string? expectedRegisteredPhysicalObjectIdentity,
                FilePublicationSourceProof expectedSourceProof,
                bool isCompanionFile = false,
                int? companionAudiobookId = null);

        /// <summary>
        /// Completes a staged move by retiring only the verified source generation
        /// while preserving the destination generation held by the registration lease.
        /// </summary>
        Task<bool> CompletePreparedMoveAsync(
            string source,
            string destination,
            IAudiobookFileRegistrationLease registrationLease,
            Guid operationId);
    }
}
