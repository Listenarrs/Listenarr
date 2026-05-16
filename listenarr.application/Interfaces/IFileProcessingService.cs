using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Handles file processing, organization, and validation for completed downloads
    /// </summary>
    public interface IFileProcessingService
    {
        /// <summary>
        /// Processes a completed download (moves files, applies metadata)
        /// </summary>
        /// <param name="downloadId">The download ID to process</param>
        Task ProcessCompletedDownloadAsync(string downloadId);

        /// <summary>
        /// Organizes a file using configured naming patterns
        /// </summary>
        /// <param name="sourceFilePath">Source file path</param>
        /// <param name="metadata">Audiobook metadata</param>
        /// <returns>Final organized file path</returns>
        Task<string> OrganizeFileAsync(string sourceFilePath, AudioMetadata metadata);

        /// <summary>
        /// Validates that a file is a valid audiobook file
        /// </summary>
        /// <param name="filePath">Path to the file to validate</param>
        /// <returns>True if valid, false otherwise</returns>
        Task<bool> ValidateFileAsync(string filePath);

        /// <summary>
        /// Cleans up old temporary files
        /// </summary>
        Task CleanupTempFilesAsync();
    }
}
