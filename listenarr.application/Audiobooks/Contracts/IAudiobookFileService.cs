
namespace Listenarr.Application.Audiobooks.Contracts
{
    public enum RegistrationPublicationCompletion
    {
        Completed,
        CommittedCleanupPending
    }

    public enum RegistrationPublicationMatchOutcome
    {
        Match,
        Mismatch,
        Unavailable
    }

    public interface IAudiobookFileRegistrationPublicationProbe
    {
        RegistrationPublicationMatchOutcome ProbeCurrentPublication();
    }

    public interface IAudiobookFileRegistrationIdentityVerifier
    {
        bool MatchesPhysicalObjectIdentity(string expectedPhysicalObjectIdentity);
    }

    public static class AudiobookFileRegistrationLeaseExtensions
    {
        public static bool MatchesPhysicalObjectIdentity(
            this IAudiobookFileRegistrationLease lease,
            string expectedPhysicalObjectIdentity)
        {
            ArgumentNullException.ThrowIfNull(lease);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedPhysicalObjectIdentity);
            if (!lease.HasDurablePhysicalObjectIdentity)
            {
                return false;
            }

            return lease is IAudiobookFileRegistrationIdentityVerifier verifier
                ? verifier.MatchesPhysicalObjectIdentity(expectedPhysicalObjectIdentity)
                : string.Equals(
                    lease.PhysicalObjectIdentity,
                    expectedPhysicalObjectIdentity,
                    StringComparison.Ordinal);
        }
    }

    public interface IAudiobookFileRegistrationLease : IDisposable
    {
        string PublicPath { get; }
        string MetadataPath { get; }
        string PhysicalObjectIdentity { get; }
        bool HasDurablePhysicalObjectIdentity => true;
        string? SourcePhysicalObjectIdentity { get; }
        Stream OpenMetadataReadStream() =>
            throw new NotSupportedException(
                "This registration lease does not expose generation-bound metadata reads.");
        Stream OpenMetadataWriteStream() =>
            throw new NotSupportedException(
                "This registration lease does not expose generation-bound metadata writes.");
        bool MatchesCurrentPublication();
        bool PrepareCleanupRecovery(int audiobookId);
        RegistrationPublicationCompletion CompletePublication();
        Task<bool> MatchesContentAsync(
            Stream candidateStream,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Manages audio file metadata extraction and database tracking
    /// </summary>
    public interface IAudiobookFileService
    {
        /// <summary>
        /// Ensure an Audiobook file record exists for the given audiobook and file path. Extract metadata and persist file-level metadata.
        /// </summary>
        /// <param name="audiobook">The audiobook</param>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="source">Optional source identifier (e.g., "scan", "import")</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True when a new ownership row was created; false when the file was already owned or could not be claimed.</returns>
        Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            string filePath,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            IAudiobookFileRegistrationLease registrationLease,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> RefreshPhysicalGenerationAsync(
            Audiobook audiobook,
            int fileId,
            string? expectedPhysicalObjectIdentity,
            IAudiobookFileRegistrationLease registrationLease,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> RollbackPhysicalGenerationClaimAsync(
            Audiobook audiobook,
            int fileId,
            string? expectedPath,
            string expectedPhysicalObjectIdentity,
            CancellationToken cancellationToken = default);

        Task<bool> RegisterPublishedGenerationAsync(
            Audiobook audiobook,
            AudiobookFileOwnershipCheckResult initialOwnership,
            IAudiobookFileRegistrationLease registrationLease,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> RegisterPublishedGenerationWithBasePathAsync(
            Audiobook audiobook,
            AudiobookFileOwnershipCheckResult initialOwnership,
            IAudiobookFileRegistrationLease registrationLease,
            string authoritativeBasePath,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> RegisterCompatibilityPublicationAsync(
            Audiobook audiobook,
            AudiobookFileOwnershipCheckResult initialOwnership,
            IAudiobookFileRegistrationLease registrationLease,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> RegisterCompatibilityPublicationWithBasePathAsync(
            Audiobook audiobook,
            AudiobookFileOwnershipCheckResult initialOwnership,
            IAudiobookFileRegistrationLease registrationLease,
            string authoritativeBasePath,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task RollbackPublishedGenerationIfStaleAsync(
            Audiobook audiobook,
            IAudiobookFileRegistrationLease registrationLease);

        Task<AudiobookFileOwnershipCheckResult> CheckAudiobookFileOwnershipAsync(
            Audiobook audiobook,
            string plannedPhysicalPath,
            string? plannedBasePath = null,
            CancellationToken cancellationToken = default);

        Task<AudiobookFileClaimResult> ClaimAudiobookFileAsync(
            Audiobook audiobook,
            AudiobookFile file,
            string physicalPath,
            CancellationToken cancellationToken = default);
    }
}
