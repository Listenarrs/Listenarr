
namespace Listenarr.Application.Audiobooks.Contracts
{
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
        /// <returns>True if the audiobook is associated with an audiobook file, false otherwise</returns>
        Task<bool> EnsureAudiobookFileAsync(Audiobook audiobook, string filePath, string? source = "scan");

        /// <summary>
        /// Delete a single tracked file from an audiobook. Disk-delete failures surface
        /// as warnings; the DB row is still removed. A history entry records the removal.
        /// </summary>
        /// <param name="audiobook">The owning audiobook. The file must belong to this audiobook or the call returns <see cref="Files.DeleteAudiobookFileOutcome.DoesNotBelongToAudiobook"/>.</param>
        /// <param name="fileId">The AudiobookFile row ID to delete.</param>
        /// <param name="deleteFromDisk">When true, also attempt to delete the file from disk.</param>
        /// <param name="source">Optional source identifier for the history entry (e.g., "manual").</param>
        /// <param name="ct">Cancellation token.</param>
        Task<Files.DeleteAudiobookFileResult> DeleteAudiobookFileAsync(Audiobook audiobook, int fileId, bool deleteFromDisk, string? source = "manual", CancellationToken ct = default);
    }
}
