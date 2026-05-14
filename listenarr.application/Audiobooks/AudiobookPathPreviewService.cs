using System.Text.RegularExpressions;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Domain.Utils;

namespace Listenarr.Application.Audiobooks
{
    public class AudiobookPathPreviewService(
        IApplicationSettingsRepository settingsRepository,
        INamingPatternService namingPatternService) : IAudiobookPathPreviewService
    {
        public async Task<PathPreviewResult> PreviewAsync(
            Audiobook audiobook,
            string? destinationRoot = null,
            CancellationToken ct = default)
        {
            var settings = await settingsRepository.GetAsync(ct) ?? new ApplicationSettings();
            var root = !string.IsNullOrWhiteSpace(destinationRoot)
                ? destinationRoot
                : settings.OutputPath;

            var namingPattern = settings.FolderNamingPattern;

            var full = ComputeBaseDirectoryFromPattern(audiobook, root ?? string.Empty, namingPattern);
            var relative = full;

            if (!string.IsNullOrEmpty(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                relative = full.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return new PathPreviewResult(full, relative, root ?? string.Empty);
        }

        private string ComputeBaseDirectoryFromPattern(
            Audiobook audiobook,
            string rootPath,
            string folderNamingPattern)
        {
            var directoryPattern = NormalizeFolderPattern(folderNamingPattern);
            var relative = namingPatternService.ApplyAudiobookNamingPattern(directoryPattern, audiobook);

            if (string.IsNullOrWhiteSpace(relative))
            {
                return rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return FileUtils.CombineWithOptionalBase(rootPath, relative);
        }

        private static string NormalizeFolderPattern(string folderNamingPattern)
        {
            if (string.IsNullOrWhiteSpace(folderNamingPattern))
            {
                return string.Empty;
            }

            var directoryPattern = folderNamingPattern;
            directoryPattern = Regex.Replace(directoryPattern, @"\{DiskNumber[^}]*\}", string.Empty, RegexOptions.IgnoreCase);
            directoryPattern = Regex.Replace(directoryPattern, @"\{ChapterNumber[^}]*\}", string.Empty, RegexOptions.IgnoreCase);
            directoryPattern = Regex.Replace(directoryPattern, @"[\\/]\s*[\\/]", "/");
            directoryPattern = Regex.Replace(directoryPattern, @"^\s*[\\/]", string.Empty);
            directoryPattern = Regex.Replace(directoryPattern, @"[\\/]\s*$", string.Empty);

            return directoryPattern;
        }
    }
}
