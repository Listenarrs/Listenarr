using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models.Naming;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Options controlling how <see cref="IFileNamingService.BuildPath"/> assembles a path.
    /// </summary>
    public sealed record NamingOptions
    {
        /// <summary>Root the relative path is combined with (e.g. the configured output path or a root folder).</summary>
        public string OutputRoot { get; init; } = string.Empty;

        /// <summary>
        /// When true, the destination folder is already decided (the audiobook has a custom BasePath), so only
        /// the file naming pattern is applied — the folder pattern is skipped.
        /// </summary>
        public bool IsCustomBasePath { get; init; }

        /// <summary>Whether this file is part of a multi-file audiobook (selects the multi-file naming pattern).</summary>
        public bool IsMultiFile { get; init; }

        /// <summary>Sequence number used to disambiguate multi-file names when the pattern has no Disk/Chapter token.</summary>
        public int? SequenceNumber { get; init; }

        /// <summary>File extension to ensure on the result (e.g. ".m4b").</summary>
        public string Extension { get; init; } = ".m4b";
    }

    /// <summary>Result of <see cref="IFileNamingService.BuildPath"/>: both the relative path and the full path.</summary>
    public sealed record NamingResult(string RelativePath, string FullPath);

    /// <summary>
    /// Generates file paths using configured naming patterns
    /// </summary>
    public interface IFileNamingService
    {
        /// <summary>
        /// Build the relative directory for an audiobook from the configured folder pattern only (no filename).
        /// Used when computing an audiobook's BasePath.
        /// </summary>
        string BuildDirectory(NamingContext context, ApplicationSettings settings);

        /// <summary>
        /// Build the relative and full path for a single audiobook file, applying the folder + file naming
        /// patterns, multi-file handling, sanitization and platform path-length limits.
        /// </summary>
        NamingResult BuildPath(NamingContext context, ApplicationSettings settings, NamingOptions options);

        /// <summary>
        /// Apply the configured file naming pattern to generate the final file path
        /// </summary>
        /// <param name="metadata">Audiobook metadata</param>
        /// <param name="originalExtension">File extension (e.g., ".m4b", ".mp3")</param>
        /// <returns>Full file path using the naming pattern</returns>
        Task<string> GenerateFilePathAsync(AudioMetadata metadata, string originalExtension = ".m4b");

        /// <summary>
        /// Apply the configured file naming pattern to generate the final file path with a specific output path
        /// </summary>
        /// <param name="metadata">Audiobook metadata</param>
        /// <param name="outputPath">Specific output path to use</param>
        /// <param name="originalExtension">File extension (e.g., ".m4b", ".mp3")</param>
        /// <returns>Full file path using the naming pattern</returns>
        Task<string> GenerateFilePathAsync(AudioMetadata metadata, string outputPath, string originalExtension = ".m4b");

        /// <summary>
        /// Parse a naming pattern and replace variables with actual values
        /// </summary>
        /// <param name="pattern">The naming pattern template</param>
        /// <param name="variables">Dictionary of variable values</param>
        /// <param name="treatAsFilename">Whether to treat as filename (sanitize invalid chars)</param>
        /// <returns>Final path with variables replaced</returns>
        string ApplyNamingPattern(string pattern, Dictionary<string, object> variables, bool treatAsFilename = false); // FIXME: Should be private
        string ApplyNamingPattern(string pattern, AudioMetadata metadata, bool treatAsFilename = false);
        string ApplyNamingPattern(string pattern, AudibleBookMetadata metadata, bool treatAsFilename = false);
    }
}
