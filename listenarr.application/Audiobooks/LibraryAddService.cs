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
using System.Security.Cryptography;
using System.Text;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks
{
    public class LibraryAddService : ILibraryAddService
    {
        private readonly IAudiobookRepository _repo;
        private readonly IHistoryRepository _historyRepository;
        private readonly IImageCacheService _imageCacheService;
        private readonly ILogger<LibraryAddService> _logger;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly AudibleService _audibleService;
        private readonly IConfigurationService _configurationService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IRootFolderService _rootFolderService;
        private readonly INotificationService? _notificationService;

        public LibraryAddService(
            IAudiobookRepository repo,
            IHistoryRepository historyRepository,
            IImageCacheService imageCacheService,
            ILogger<LibraryAddService> logger,
            IQualityProfileService qualityProfileService,
            AudibleService audibleService,
            IConfigurationService configurationService,
            IFileNamingService fileNamingService,
            IRootFolderService rootFolderService,
            INotificationService? notificationService = null)
        {
            _repo = repo;
            _historyRepository = historyRepository;
            _imageCacheService = imageCacheService;
            _logger = logger;
            _qualityProfileService = qualityProfileService;
            _audibleService = audibleService;
            _configurationService = configurationService;
            _fileNamingService = fileNamingService;
            _rootFolderService = rootFolderService;
            _notificationService = notificationService;
        }

        public async Task<LibraryAddOperationResult> AddToLibraryAsync(
            LibraryAddOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var metadata = request.Metadata ?? new AudibleBookMetadata();

            _logger.LogInformation(
                "LibraryAddService received metadata: Title={Title}, Asin={Asin}, PublishYear={PublishYear}, Authors={Authors}, Series={Series}",
                metadata.Title,
                metadata.Asin,
                metadata.PublishYear,
                metadata.Authors != null ? string.Join(", ", metadata.Authors) : "null",
                metadata.Series);

            if (string.IsNullOrWhiteSpace(metadata.PublishYear) && request.SearchResult != null)
            {
                try
                {
                    if (DateTime.TryParse(request.SearchResult.PublishedDate, out var publishDate))
                    {
                        metadata.PublishYear = publishDate.Year.ToString();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to extract publish year from search result publishedDate");
                }
            }

            if (!string.IsNullOrWhiteSpace(metadata.Asin))
            {
                var existingByAsin = await _repo.GetByAsinAsync(metadata.Asin);
                if (existingByAsin != null)
                {
                    return new LibraryAddOperationResult
                    {
                        AlreadyExists = true,
                        Message = "Audiobook already exists in library",
                        Audiobook = existingByAsin
                    };
                }
            }

            var firstIsbn = (metadata.Isbn ?? Enumerable.Empty<string>())
                .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                if (existingByIsbn != null)
                {
                    return new LibraryAddOperationResult
                    {
                        AlreadyExists = true,
                        Message = "Audiobook already exists in library",
                        Audiobook = existingByIsbn
                    };
                }
            }

            var imageUrl = await MoveImageToLibraryStorageAsync(metadata, request.SearchResult, firstIsbn);

            var audiobook = metadata.ToAudiobook();

            audiobook.ImageUrl = imageUrl;
            audiobook.Monitored = request.Monitored;

            SyncImportedIdentifiersFromLegacyFields(audiobook);

            if (request.QualityProfileId.HasValue)
            {
                audiobook.QualityProfileId = request.QualityProfileId.Value;
            }
            else
            {
                var defaultProfile = await _qualityProfileService.GetDefaultAsync();
                if (defaultProfile != null)
                {
                    audiobook.QualityProfileId = defaultProfile.Id;
                }
                else
                {
                    _logger.LogWarning(
                        "No default quality profile found. New audiobook '{Title}' will not have a quality profile assigned.",
                        audiobook.Title);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _repo.AddAsync(audiobook);

            await ResolveAuthorAsinsAsync(audiobook);
            await SendAddedNotificationAsync(audiobook);
            await AddHistoryEntryAsync(audiobook, request, cancellationToken);

            var settings = await _configurationService.GetApplicationSettingsAsync();

            // Check validity of given path
            var baseDirectory = request.DestinationPath;
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                try
                {
                    Path.GetFullPath(baseDirectory);
                }
                catch (Exception)
                {
                    baseDirectory = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                var rootFolder = await _rootFolderService.GetDefaultAsync();
                baseDirectory = rootFolder != null ? rootFolder.Path : settings.OutputPath;

                audiobook.BasePath = Path.Join(baseDirectory, _fileNamingService.ApplyNamingPattern(settings.FolderNamingPattern, metadata));
            }
            else
            {
                audiobook.BasePath = baseDirectory;
            }

            _logger.LogInformation(
                "Added audiobook '{Title}' (ASIN: {Asin}) to library with Monitored={Monitored}, QualityProfileId={QualityProfileId}, AutoSearch={AutoSearch}",
                audiobook.Title,
                audiobook.Asin,
                request.Monitored,
                audiobook.QualityProfileId,
                request.AutoSearch);

            return new LibraryAddOperationResult
            {
                Added = true,
                Message = "Audiobook added to library successfully",
                Audiobook = audiobook
            };
        }

        private async Task<string?> MoveImageToLibraryStorageAsync(
            AudibleBookMetadata metadata,
            SearchResult? searchResult,
            string? firstIsbn)
        {
            var imageUrl = metadata.ImageUrl;

            if (!string.IsNullOrWhiteSpace(metadata.Asin))
            {
                return await TryMoveImageAsync(metadata.Asin, metadata.ImageUrl) ?? imageUrl;
            }

            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                var derivedKey = "img-" + ComputeShortHash(firstIsbn);
                return await TryMoveImageAsync(derivedKey, metadata.ImageUrl) ?? imageUrl;
            }

            if (!string.IsNullOrWhiteSpace(metadata.ImageUrl))
            {
                var rawKey = searchResult?.Id ?? searchResult?.ResultUrl ?? searchResult?.ProductUrl ?? metadata.ImageUrl;
                var derivedKey = "img-" + ComputeShortHash(rawKey);
                return await TryMoveImageAsync(derivedKey, metadata.ImageUrl) ?? imageUrl;
            }

            return imageUrl;
        }

        private async Task<string?> TryMoveImageAsync(string imageKey, string? sourceImageUrl)
        {
            try
            {
                var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(imageKey, sourceImageUrl);
                return string.IsNullOrWhiteSpace(libraryImagePath) ? null : $"/{libraryImagePath}";
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Error moving image for key {ImageKey} to library storage", imageKey);
            }

            return null;
        }

        private async Task ResolveAuthorAsinsAsync(Audiobook audiobook)
        {
            try
            {
                if (audiobook.Authors == null || !audiobook.Authors.Any())
                {
                    return;
                }

                audiobook.AuthorAsins = audiobook.AuthorAsins ?? new List<string>();
                foreach (var authorName in audiobook.Authors)
                {
                    try
                    {
                        var info = await _audibleService.LookupAuthorAsync(authorName);
                        if (info == null || string.IsNullOrWhiteSpace(info.Asin))
                        {
                            continue;
                        }

                        if (!audiobook.AuthorAsins.Contains(info.Asin))
                        {
                            audiobook.AuthorAsins.Add(info.Asin);
                        }

                        try
                        {
                            var moved = await _imageCacheService.MoveToAuthorLibraryStorageAsync(info.Asin, info.Image);
                            if (moved != null)
                            {
                                _logger.LogInformation(
                                    "Cached author image for {Author} (ASIN: {Asin})",
                                    authorName,
                                    info.Asin);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to cache author image for {Author}", authorName);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Author lookup failed for {Author}", authorName);
                    }
                }

                await _repo.UpdateAsync(audiobook);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving author ASINs for audiobook '{Title}'", audiobook.Title);
            }
        }

        private async Task SendAddedNotificationAsync(Audiobook audiobook)
        {
            if (_notificationService == null)
            {
                return;
            }

            var settings = await _configurationService.GetApplicationSettingsAsync();
            var data = new
            {
                id = audiobook.Id,
                title = audiobook.Title ?? "Unknown Title",
                authors = audiobook.Authors,
                narrators = audiobook.Narrators,
                description = audiobook.Description,
                asin = audiobook.Asin,
                publisher = audiobook.Publisher,
                year = audiobook.PublishYear,
                imageUrl = audiobook.ImageUrl
            };

            await _notificationService.SendNotificationAsync(
                "book-added",
                data,
                settings.WebhookUrl,
                settings.EnabledNotificationTriggers);
        }

        private async Task AddHistoryEntryAsync(
            Audiobook audiobook,
            LibraryAddOperationRequest request,
            CancellationToken cancellationToken)
        {
            var historyEntry = new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown Title",
                EventType = "Added",
                Message = request.HistoryMessage ??
                    $"Audiobook '{audiobook.Title}' added to library from {request.HistorySource}",
                Source = request.HistorySource,
                Timestamp = DateTime.UtcNow
            };

            await _historyRepository.AddAsync(historyEntry, cancellationToken);
        }

        private static string? ToStringOrFirst(object? value)
        {
            if (value is List<string> list)
            {
                return list.FirstOrDefault();
            }

            return value as string;
        }

        private static string ComputeShortHash(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return Guid.NewGuid().ToString("N").Substring(0, 12);
            }

            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA1.HashData(bytes);
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        private static void SyncImportedIdentifiersFromLegacyFields(Audiobook audiobook)
        {
            audiobook.ExternalIdentifiers ??= new List<AudiobookExternalIdentifier>();

            audiobook.ExternalIdentifiers = audiobook.ExternalIdentifiers
                .Where(i => i.Source != AudiobookExternalIdentifierSource.Imported)
                .ToList();

            var existingTypeValueKeys = new HashSet<string>(
                audiobook.ExternalIdentifiers
                    .Where(i => !string.IsNullOrWhiteSpace(i.ValueNormalized))
                    .Select(IdentifierTypeValueKey),
                StringComparer.OrdinalIgnoreCase);
            var seenImportedFullKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var imported = BuildLegacyBackfillIdentifiers(audiobook, AudiobookExternalIdentifierSource.Imported);
            foreach (var item in imported.Where(item =>
                         !string.IsNullOrWhiteSpace(item.ValueNormalized) &&
                         !existingTypeValueKeys.Contains(IdentifierTypeValueKey(item)) &&
                         seenImportedFullKeys.Add(IdentifierFullKey(item))))
            {
                audiobook.ExternalIdentifiers.Add(item);
            }
        }

        private static List<AudiobookExternalIdentifier> BuildLegacyBackfillIdentifiers(
            Audiobook audiobook,
            AudiobookExternalIdentifierSource source)
        {
            var now = DateTime.UtcNow;
            var result = new List<AudiobookExternalIdentifier>();

            if (!string.IsNullOrWhiteSpace(audiobook.Asin) &&
                AudiobookIdentifierNormalizer.TryNormalize(
                    AudiobookExternalIdentifierType.Asin,
                    audiobook.Asin,
                    out var normalizedAsin,
                    out _))
            {
                result.Add(new AudiobookExternalIdentifier
                {
                    Type = AudiobookExternalIdentifierType.Asin,
                    ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(audiobook.Asin),
                    ValueNormalized = normalizedAsin,
                    Region = null,
                    IsPrimary = true,
                    Source = source,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            var seenIsbns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var isbn in audiobook.Isbn ?? new List<string>())
            {
                if (!AudiobookIdentifierNormalizer.TryNormalize(
                        AudiobookExternalIdentifierType.Isbn,
                        isbn,
                        out var normalizedIsbn,
                        out _))
                {
                    continue;
                }

                if (!seenIsbns.Add(normalizedIsbn))
                {
                    continue;
                }

                result.Add(new AudiobookExternalIdentifier
                {
                    Type = AudiobookExternalIdentifierType.Isbn,
                    ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(isbn),
                    ValueNormalized = normalizedIsbn,
                    Region = null,
                    IsPrimary = false,
                    Source = source,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            if (!string.IsNullOrWhiteSpace(audiobook.OpenLibraryId) &&
                AudiobookIdentifierNormalizer.TryNormalize(
                    AudiobookExternalIdentifierType.OpenLibraryId,
                    audiobook.OpenLibraryId,
                    out var normalizedOlid,
                    out _))
            {
                result.Add(new AudiobookExternalIdentifier
                {
                    Type = AudiobookExternalIdentifierType.OpenLibraryId,
                    ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(audiobook.OpenLibraryId),
                    ValueNormalized = normalizedOlid,
                    Region = null,
                    IsPrimary = true,
                    Source = source,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            return result;
        }

        private static string IdentifierTypeValueKey(AudiobookExternalIdentifier item)
        {
            return $"{item.Type}|{item.ValueNormalized}";
        }

        private static string IdentifierFullKey(AudiobookExternalIdentifier item)
        {
            return $"{item.Type}|{item.ValueNormalized}|{item.Region ?? string.Empty}";
        }
    }
}
