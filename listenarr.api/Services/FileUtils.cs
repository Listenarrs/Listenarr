using System;
using System.Collections.Generic;
using System.IO;

namespace Listenarr.Api.Services
{
    internal static class FileUtils
    {
        /// <summary>
        /// Audio file extensions recognized by the import and scan pipelines.
        /// Centralized here so that the scan service, import service, and completed download
        /// processor all use the same set – preventing non-audio files (cover images, NFOs, etc.)
        /// from being registered as AudiobookFile records only to be removed on the next scan.
        /// </summary>
        public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".m4b", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav",
            ".wv", ".wma", ".ape", ".alac", ".aif", ".aiff"
        };

        /// <summary>
        /// Returns true when the file path has a recognized audio extension.
        /// </summary>
        public static bool IsAudioFile(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return !string.IsNullOrEmpty(ext) && AudioExtensions.Contains(ext);
        }

        /// <summary>
        /// Generate a unique destination path by appending " (1)", " (2)", ... before the extension
        /// when the candidate already exists either on disk or in an in-memory set of used paths.
        /// </summary>
        public static string GetUniqueDestinationPath(string desiredPath, Func<string, bool>? existsPredicate = null, ISet<string>? inMemoryUsed = null)
        {
            try
            {
                existsPredicate ??= File.Exists;

                if (!existsPredicate(desiredPath) && (inMemoryUsed == null || !inMemoryUsed.Contains(desiredPath)))
                    return desiredPath;

                var dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
                var name = Path.GetFileNameWithoutExtension(desiredPath);
                var ext = Path.GetExtension(desiredPath);
                var idx = 1;
                string candidate;
                do
                {
                    candidate = Path.Combine(dir, $"{name} ({idx}){ext}");
                    idx++;
                }
                while (existsPredicate(candidate) || (inMemoryUsed != null && inMemoryUsed.Contains(candidate)));

                return candidate;
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
                return desiredPath;
            }
        }
    }
}
