using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
{
    public class AudiobookshelfImportService : IAudiobookshelfImportService
    {
        private readonly IAudiobookshelfService _audiobookshelfService;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IScanQueueService _scanQueueService;
        private readonly IMetadataService _metadataService;
        private readonly ILogger<AudiobookshelfImportService> _logger;

        private readonly IRootFolderService _rootFolderService;

        public AudiobookshelfImportService(
            IAudiobookshelfService audiobookshelfService,
            IAudiobookRepository audiobookRepository,
            IRootFolderService rootFolderService,
            IScanQueueService scanQueueService,
            IMetadataService metadataService,
            ILogger<AudiobookshelfImportService> logger)
        {
            _audiobookshelfService = audiobookshelfService;
            _audiobookRepository = audiobookRepository;
            _rootFolderService = rootFolderService;
            _scanQueueService = scanQueueService;
            _metadataService = metadataService;
            _logger = logger;
        }

        public async Task<IReadOnlyList<AudiobookshelfImportPreviewDto>> PreviewImportAsync(
            string libraryId,
            CancellationToken ct = default)
        {
            var items = await _audiobookshelfService.GetLibraryItemsAsync(libraryId, ct);
            var previews = new List<AudiobookshelfImportPreviewDto>();

            foreach (var item in items)
            {
                var existing = await FindExistingAsync(item, ct);

                previews.Add(new AudiobookshelfImportPreviewDto
                {
                    ItemId = item.Id,
                    Title = item.Metadata.Title ?? "(untitled)",
                    Author = item.Metadata.Authors.FirstOrDefault() ?? string.Empty,
                    Path = item.Path,
                    ExistingAudiobookId = existing?.Id,
                    WillImport = existing == null,
                    Reason = existing == null
                        ? "Ready to import"
                        : $"Already exists in Listenarr (ID {existing.Id})",
                    Asin = item.Metadata.Asin,
                    Isbn = item.Metadata.Isbn
                });
            }

            return previews;
        }

        private async Task<string> ResolveListenarrBasePathAsync(
            string libraryId,
            string absItemPath,
            bool isFile,
            CancellationToken ct = default)
        {
            var rootFolders = await _rootFolderService.GetAllAsync();
            var mappedRoot = rootFolders.FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(r.AudiobookshelfLibraryId) &&
                string.Equals(r.AudiobookshelfLibraryId, libraryId, StringComparison.OrdinalIgnoreCase));

            if (mappedRoot == null || string.IsNullOrWhiteSpace(mappedRoot.Path))
            {
                _logger.LogWarning(
                    "No mapped Listenarr root folder found for Audiobookshelf library {LibraryId}. Using original path {Path}",
                    libraryId,
                    absItemPath);

                return absItemPath;
            }

            var libraries = await _audiobookshelfService.GetLibrariesAsync(ct);
            var absLibrary = libraries.FirstOrDefault(l =>
                string.Equals(l.Id, libraryId, StringComparison.OrdinalIgnoreCase));

            var absPathToTranslate = isFile
                ? Path.GetDirectoryName(absItemPath) ?? absItemPath
                : absItemPath;

            string absLibraryRoot;

            if (absLibrary != null && !string.IsNullOrWhiteSpace(absLibrary.Path))
            {
                absLibraryRoot = absLibrary.Path;
            }
            else
            {
                var segments = absPathToTranslate
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();

                if (segments.Length < 2)
                {
                    _logger.LogWarning(
                        "Unable to derive ABS root for {Path}. Falling back to mapped root {RootPath}",
                        absItemPath,
                        mappedRoot.Path);

                    return mappedRoot.Path;
                }

                absLibraryRoot = Path.DirectorySeparatorChar +
                    Path.Combine(segments.Take(2).ToArray());

                _logger.LogWarning(
                    "ABS library path missing. Derived root {DerivedRoot} from {Path}",
                    absLibraryRoot,
                    absItemPath);
            }

            try
            {
                var relative = Path.GetRelativePath(absLibraryRoot, absPathToTranslate);

                if (relative.StartsWith(".."))
                {
                    _logger.LogWarning(
                        "ABS item path {ItemPath} is not under ABS library root {LibraryPath}. Falling back to mapped root {RootPath}",
                        absItemPath,
                        absLibraryRoot,
                        mappedRoot.Path);

                    return mappedRoot.Path;
                }

                return Path.Combine(mappedRoot.Path, relative);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to translate ABS path {ItemPath} using library root {LibraryPath}. Falling back to mapped root {RootPath}",
                    absItemPath,
                    absLibraryRoot,
                    mappedRoot.Path);

                return mappedRoot.Path;
            }
        }

        
        public async Task<AudiobookshelfImportResultDto> ImportAsync(
            AudiobookshelfImportRequestDto request,
            CancellationToken ct = default)
        {
            var result = new AudiobookshelfImportResultDto();
            var items = await _audiobookshelfService.GetLibraryItemsAsync(request.LibraryId, ct);

            var selectedItems = items
                .Where(i => request.ItemIds.Contains(i.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var rootFolders = await _rootFolderService.GetAllAsync();
            var mappedRoot = rootFolders.FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(r.AudiobookshelfLibraryId) &&
                string.Equals(r.AudiobookshelfLibraryId, request.LibraryId, StringComparison.OrdinalIgnoreCase));


            foreach (var item in selectedItems)
            {
                var existing = await FindExistingAsync(item, ct);
                if (existing != null && request.SkipExisting)
                {
                    result.SkippedCount++;
                    result.Messages.Add($"Skipped '{item.Metadata.Title}' because it already exists.");
                    continue;
                }

                var (translatedBasePath, translatedFilePath) =
                    await ResolveListenarrPathsAsync(request.LibraryId, item.Path, item.IsFile, ct);

                // Start with ABS metadata
                var merged = CloneMetadata(item.Metadata);

                // Enrich from file tags for single-file imports
                AudioMetadata? extractedMetadata = null;
                if (item.IsFile && !string.IsNullOrWhiteSpace(translatedFilePath))
                {
                    extractedMetadata = await _metadataService.ExtractFileMetadataAsync(translatedFilePath);
                    if (extractedMetadata != null)
                    {
                        MergeFromAudioMetadata(merged, extractedMetadata);
                    }
                }

                // Enrich from online metadata
                var lookupTitle = merged.Title ?? extractedMetadata?.Title ?? "Unknown Title";
                var lookupAuthor = merged.Authors.FirstOrDefault()
                  ?? (!string.IsNullOrWhiteSpace(extractedMetadata?.Artist) ? extractedMetadata.Artist : null)
                  ?? (!string.IsNullOrWhiteSpace(extractedMetadata?.AlbumArtist) ? extractedMetadata.AlbumArtist : null);

                var lookupIsbn = merged.Isbn.FirstOrDefault() ?? extractedMetadata?.Isbn;

                AudioMetadata? onlineMetadata = null;
                try
                {
                    onlineMetadata = await _metadataService.GetMetadataAsync(
                        lookupTitle,
                        lookupAuthor,
                        lookupIsbn);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Online metadata lookup failed for title={Title}, author={Author}, isbn={Isbn}",
                        lookupTitle,
                        lookupAuthor,
                        lookupIsbn);
                }

                if (onlineMetadata != null)
                {
                    MergeFromAudioMetadata(merged, onlineMetadata);
                }

                var audiobook = new Audiobook
                {
                    Title = merged.Title ?? "Unknown Title",
                    Subtitle = merged.Subtitle,
                    Authors = merged.Authors,
                    Narrators = merged.Narrators,
                    Series = merged.Series,
                    SeriesNumber = merged.SeriesNumber,
                    Description = merged.Description,
                    Genres = merged.Genres,
                    Tags = merged.Tags,
                    Isbn = merged.Isbn,
                    Asin = merged.Asin,
                    Publisher = merged.Publisher,
                    Language = merged.Language,
                    Runtime = merged.Runtime,
                    Explicit = merged.Explicit,
                    Abridged = merged.Abridged,
                    PublishYear = merged.PublishedYear,
                    PublishedDate = merged.PublishedDate,
                    ImageUrl = merged.ImageUrl,
                    BasePath = translatedBasePath,
                    FilePath = translatedFilePath,
                    FileSize = item.IsFile ? item.Size : null,
                    Files = item.IsFile && !string.IsNullOrWhiteSpace(translatedFilePath)
                        ? new List<AudiobookFile>
                        {
                            new AudiobookFile
                            {
                                Path = Path.GetFileName(translatedFilePath),
                                Size = item.Size,
                                DurationSeconds = merged.Runtime,
                                Format = Path.GetExtension(translatedFilePath)?.TrimStart('.'),
                                Source = "Audiobookshelf Import"
                            }
                        }
                        : null,
                    Monitored = request.Monitored
                };

                await _audiobookRepository.AddAsync(audiobook);
                var scanPath = item.IsFile && !string.IsNullOrWhiteSpace(translatedFilePath)
                    ? translatedFilePath
                    : translatedBasePath;
                _logger.LogDebug(
                    "Imported audiobook '{Title}' (ID {AudiobookId}) from ABS library {LibraryId}. Enqueuing focused scan {ScanPath}, translatedFilePath={TranslatedFilePath}, translatedBasePath={TranslatedBasePath}",
                    audiobook.Title,
                    audiobook.Id,
                    request.LibraryId,
                    scanPath,
                    translatedFilePath,
                    translatedBasePath );
                await EnqueueFocusedScanAsync(audiobook.Id, scanPath);

                result.ImportedCount++;
                result.Messages.Add($"Imported '{audiobook.Title}'.");
            }

            return result;
        }

        private async Task EnqueueFocusedScanAsync(int audiobookId, string? scanPath)
        {
            if (_scanQueueService == null)
            {
                _logger.LogWarning("ScanQueueService not available");
                return;
            }

            if (string.IsNullOrWhiteSpace(scanPath))
            {
                _logger.LogDebug(
                    "No focused scan path could be determined for audiobook {AudiobookId} after Audiobookshelf import",
                    audiobookId);
                return;
            }

            try
            {
                var scanJobId = await _scanQueueService.EnqueueScanAsync(audiobookId, scanPath);

                _logger.LogInformation(
                    "Enqueued focused scan {ScanJobId} for audiobook {AudiobookId} (path: {Path}) after Audiobookshelf import",
                    scanJobId,
                    audiobookId,
                    scanPath);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to enqueue focused scan for audiobook {AudiobookId} after Audiobookshelf import",
                    audiobookId);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to enqueue focused scan for audiobook {AudiobookId} after Audiobookshelf import",
                    audiobookId);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to enqueue focused scan for audiobook {AudiobookId} after Audiobookshelf import",
                    audiobookId);
            }
        }

        private static AudiobookshelfBookMetadataDto CloneMetadata(AudiobookshelfBookMetadataDto source)
        {
            return new AudiobookshelfBookMetadataDto
            {
                Title = source.Title,
                Subtitle = source.Subtitle,
                Authors = source.Authors?.ToList() ?? new List<string>(),
                Narrators = source.Narrators?.ToList() ?? new List<string>(),
                Series = source.Series,
                SeriesNumber = source.SeriesNumber,
                Publisher = source.Publisher,
                Language = source.Language,
                Asin = source.Asin,
                Isbn = source.Isbn?.ToList() ?? new List<string>(),
                PublishedYear = source.PublishedYear,
                PublishedDate = source.PublishedDate,
                Description = source.Description,
                ImageUrl = source.ImageUrl,
                Genres = source.Genres?.ToList() ?? new List<string>(),
                Tags = source.Tags?.ToList() ?? new List<string>(),
                Runtime = source.Runtime,
                Explicit = source.Explicit,
                Abridged = source.Abridged
            };
        }

        private static void MergeFromAudioMetadata(
            AudiobookshelfBookMetadataDto target,
            AudioMetadata source)
        {
            if (string.IsNullOrWhiteSpace(target.Title) && !string.IsNullOrWhiteSpace(source.Title))
                target.Title = source.Title;

            if (string.IsNullOrWhiteSpace(target.Subtitle) && !string.IsNullOrWhiteSpace(source.Subtitle))
                target.Subtitle = source.Subtitle;

            if (!target.Authors.Any())
            {
                var authors = new List<string>();

                if (!string.IsNullOrWhiteSpace(source.Artist))
                    authors.Add(source.Artist);

                if (!string.IsNullOrWhiteSpace(source.AlbumArtist) &&
                    !authors.Contains(source.AlbumArtist, StringComparer.OrdinalIgnoreCase))
                {
                    authors.Add(source.AlbumArtist);
                }

                if (authors.Any())
                    target.Authors = authors;
            }

            if (!target.Narrators.Any() && !string.IsNullOrWhiteSpace(source.Narrator))
            {
                target.Narrators = source.Narrator
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            if (string.IsNullOrWhiteSpace(target.Series) && !string.IsNullOrWhiteSpace(source.Series))
                target.Series = source.Series;

            if (string.IsNullOrWhiteSpace(target.SeriesNumber) && source.SeriesPosition.HasValue)
                target.SeriesNumber = source.SeriesPosition.Value.ToString();

            if (string.IsNullOrWhiteSpace(target.Publisher) && !string.IsNullOrWhiteSpace(source.Publisher))
                target.Publisher = source.Publisher;

            if (string.IsNullOrWhiteSpace(target.Language) && !string.IsNullOrWhiteSpace(source.Language))
                target.Language = source.Language;

            if (string.IsNullOrWhiteSpace(target.Asin) && !string.IsNullOrWhiteSpace(source.Asin))
                target.Asin = source.Asin;

            if (!target.Isbn.Any() && !string.IsNullOrWhiteSpace(source.Isbn))
                target.Isbn = new List<string> { source.Isbn };

            if (string.IsNullOrWhiteSpace(target.PublishedYear) && source.Year.HasValue)
                target.PublishedYear = source.Year.Value.ToString();

            if (string.IsNullOrWhiteSpace(target.PublishedDate) && source.PublishDate.HasValue)
                target.PublishedDate = source.PublishDate.Value.ToString("yyyy-MM-dd");

            if (string.IsNullOrWhiteSpace(target.Description) && !string.IsNullOrWhiteSpace(source.Description))
                target.Description = source.Description;

            if (string.IsNullOrWhiteSpace(target.ImageUrl) && !string.IsNullOrWhiteSpace(source.CoverArtUrl))
                target.ImageUrl = source.CoverArtUrl;

            if (!target.Genres.Any() && !string.IsNullOrWhiteSpace(source.Genre))
            {
                target.Genres = source.Genre
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            if (!target.Runtime.HasValue && source.Duration > TimeSpan.Zero)
                target.Runtime = (int)Math.Round(source.Duration.TotalSeconds);
        }

        private async Task<(string BasePath, string? FilePath)> ResolveListenarrPathsAsync(
            string libraryId,
            string absItemPath,
            bool isFile,
            CancellationToken ct = default)
        {
            var basePath = await ResolveListenarrBasePathAsync(libraryId, absItemPath, isFile, ct);

            if (!isFile)
                return (basePath, null);

            var fileName = Path.GetFileName(absItemPath);
            if (string.IsNullOrWhiteSpace(fileName))
                return (basePath, null);

            return (basePath, Path.Combine(basePath, fileName));
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            var units = new[] { "KiB", "MiB", "GiB", "TiB" };
            double size = bytes / 1024.0;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024.0;
                unit++;
            }
            return $"{size:F1} {units[unit]}";
        }

        private async Task<Audiobook?> FindExistingAsync(
            AudiobookshelfLibraryItemDto item,
            CancellationToken ct)
        {
            var all = await _audiobookRepository.GetAllAsync();

            if (!string.IsNullOrWhiteSpace(item.Metadata.Asin))
            {
                var asinMatch = all.FirstOrDefault(a =>
                    !string.IsNullOrWhiteSpace(a.Asin) &&
                    string.Equals(a.Asin, item.Metadata.Asin, StringComparison.OrdinalIgnoreCase));

                if (asinMatch != null)
                    return asinMatch;
            }

            if (item.Metadata.Isbn.Any())
            {
                var isbnMatch = all.FirstOrDefault(a =>
                    a.Isbn != null &&
                    a.Isbn.Any(existing => item.Metadata.Isbn.Contains(existing, StringComparer.OrdinalIgnoreCase)));

                if (isbnMatch != null)
                    return isbnMatch;
            }

            var absTitle = (item.Metadata.Title ?? string.Empty).Trim();
            var absAuthor = item.Metadata.Authors.FirstOrDefault()?.Trim() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(absTitle))
            {
                var fallback = all.FirstOrDefault(a =>
                    string.Equals((a.Title ?? string.Empty).Trim(), absTitle, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((a.Authors?.FirstOrDefault() ?? string.Empty).Trim(), absAuthor, StringComparison.OrdinalIgnoreCase));

                if (fallback != null)
                    return fallback;
            }

            return null;
        }
    }
}