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


namespace Listenarr.Application.Metadata.Contracts
{
    public readonly record struct MetadataFileSource(
        string ReadPath,
        string PublicPath);

    /// <summary>
    /// Provides metadata retrieval and file tagging for audiobook files
    /// </summary>
    public interface IMetadataService
    {
        /// <summary>
        /// Retrieve metadata for the file related to the given job
        /// </summary>
        /// <param name="job">Job to process metadata for</param>
        /// <param name="download">Related download if any</param>
        /// <param name="audiobook">Related audiobook if any</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Compiled metadata from all known sources</returns>
        Task<AudioMetadata> FetchMetadataAsync(
            DownloadProcessingJob job,
            Download? download,
            Audiobook? audiobook,
            CancellationToken cancellationToken);

        /// <summary>
        /// Gets metadata from online sources
        /// </summary>
        /// <param name="title">The audiobook title</param>
        /// <param name="artist">Optional artist/author name</param>
        /// <param name="isbn">Optional ISBN</param>
        /// <returns>Audio metadata or null if not found</returns>
        Task<AudioMetadata?> GetMetadataAsync(string title, string? artist = null, string? isbn = null);

        /// <summary>
        /// Extracts metadata from an audio file using ffprobe
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <returns>Extracted audio metadata, or null if extraction failed</returns>
        Task<AudioMetadata?> ExtractFileMetadataAsync(string filePath);

        /// <summary>
        /// Extracts metadata through a stable read path while deriving fallback
        /// identity from the separate public path.
        /// </summary>
        Task<AudioMetadata?> ExtractFileMetadataAsync(
            MetadataFileSource fileSource);

        /// <summary>
        /// Applies metadata tags to an audio file
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="metadata">Metadata to apply</param>
        Task ApplyMetadataAsync(string filePath, AudioMetadata metadata);

        /// <summary>
        /// Writes the ASIN to the audio file's embedded tags. No-op if filePath or asin is null/empty.
        /// </summary>
        Task WriteAsinTagAsync(string filePath, string asin);

        /// <summary>
        /// Writes the ASIN through a generation-bound registration lease so a
        /// replacement pathname cannot receive metadata intended for the
        /// published audiobook file.
        /// </summary>
        Task WriteAsinTagAsync(
            IAudiobookFileRegistrationLease registrationLease,
            string asin) =>
            Task.FromException(
                new NotSupportedException(
                    "Generation-bound ASIN tagging is unavailable."));

        /// <summary>
        /// Downloads cover art image from URL
        /// </summary>
        /// <param name="coverArtUrl">URL of the cover art image</param>
        /// <returns>Image data as byte array or null if failed</returns>
        /// <summary>
        /// Write the tags an import should leave behind: always the ASIN, and cover artwork
        /// as well when <c>EmbedCoverArtInAudioFiles</c> is on. Both go in one open and save.
        /// </summary>
        Task WriteImportTagsAsync(
            IAudiobookFileRegistrationLease registrationLease,
            string asin,
            string? coverArtUrl);

        Task<byte[]?> DownloadCoverArtAsync(string coverArtUrl);
    }
}
