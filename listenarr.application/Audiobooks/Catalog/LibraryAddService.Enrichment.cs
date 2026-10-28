using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Catalog;

public partial class LibraryAddService
{
    private const int MaxAuthorEnrichmentCount = 32;
    private async Task<PreparedLibraryImage> PrepareLibraryImageAsync(
        AudibleBookMetadata metadata,
        SearchResult? searchResult,
        string? firstIsbn,
        CancellationToken cancellationToken)
    {
        string? imageKey = null;
        if (!string.IsNullOrWhiteSpace(metadata.Asin))
        {
            imageKey = metadata.Asin;
        }
        else if (!string.IsNullOrWhiteSpace(firstIsbn))
        {
            imageKey = "img-" + ComputeShortHash(firstIsbn);
        }
        else if (!string.IsNullOrWhiteSpace(metadata.ImageUrl))
        {
            var rawKey =
                searchResult?.Id
                ?? searchResult?.ResultUrl
                ?? searchResult?.ProductUrl
                ?? metadata.ImageUrl;
            imageKey = "img-" + ComputeShortHash(rawKey);
        }

        if (!string.IsNullOrWhiteSpace(imageKey)
            && !string.IsNullOrWhiteSpace(metadata.ImageUrl))
        {
            try
            {
                await _imageCacheService.DownloadAndCacheImageAsync(metadata.ImageUrl, imageKey);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Image preparation timed out for key {ImageKey}; retaining the source URL",
                    imageKey);
            }
            catch (Exception ex) when (ex is
                IOException or UnauthorizedAccessException or HttpRequestException
                or InvalidOperationException or UriFormatException)
            {
                _logger.LogWarning(
                    ex,
                    "Image preparation failed for key {ImageKey}; retaining the source URL",
                    imageKey);
            }
        }

        return new PreparedLibraryImage(imageKey, metadata.ImageUrl);
    }

    private async Task<string?> PublishLibraryImageAsync(PreparedLibraryImage prepared)
    {
        if (string.IsNullOrWhiteSpace(prepared.ImageKey))
        {
            return prepared.FallbackImageUrl;
        }

        return await TryMoveImageAsync(prepared.ImageKey, sourceImageUrl: null)
            ?? prepared.FallbackImageUrl;
    }

    private async Task<string?> TryMoveImageAsync(string imageKey, string? sourceImageUrl)
    {
        try
        {
            var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(
                imageKey,
                sourceImageUrl);
            return string.IsNullOrWhiteSpace(libraryImagePath) ? null : $"/{libraryImagePath}";
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or HttpRequestException
            or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            _logger.LogWarning(
                ex,
                "Error moving image for key {ImageKey} to library storage",
                imageKey);
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> EnrichAuthorAsinsAsync(
        Audiobook audiobook,
        CancellationToken cancellationToken)
    {
        var preparedAuthorImages = new List<string>();
        try
        {
            var authors = (audiobook.Authors ?? [])
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Select(author => author.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxAuthorEnrichmentCount)
                .ToArray();
            audiobook.AuthorAsins ??= new List<string>();
            foreach (var authorName in authors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = await _audibleService.LookupAuthorAsync(authorName);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (info == null || string.IsNullOrWhiteSpace(info.Asin))
                    {
                        continue;
                    }

                    if (!audiobook.AuthorAsins.Contains(info.Asin))
                    {
                        audiobook.AuthorAsins.Add(info.Asin);
                    }

                    if (!string.IsNullOrWhiteSpace(info.Image))
                    {
                        await _imageCacheService.DownloadAndCacheImageAsync(info.Image, info.Asin);
                        cancellationToken.ThrowIfCancellationRequested();
                        preparedAuthorImages.Add(info.Asin);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                    && ex is not OutOfMemoryException
                    && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Author lookup failed for {Author}", authorName);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex is not OutOfMemoryException
            && ex is not StackOverflowException)
        {
            _logger.LogWarning(
                ex,
                "Error resolving author ASINs for audiobook '{Title}'",
                audiobook.Title);
        }

        return preparedAuthorImages.Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task PublishAuthorImageAsync(string authorImageKey)
    {
        try
        {
            var moved = await _imageCacheService.MoveToAuthorLibraryStorageAsync(
                authorImageKey,
                imageUrl: null);
            if (moved != null)
            {
                _logger.LogInformation(
                    "Published cached author image for ASIN {Asin}",
                    authorImageKey);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex is not OutOfMemoryException
            && ex is not StackOverflowException)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish cached author image for ASIN {Asin}",
                authorImageKey);
        }
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

    private sealed record PreparedLibraryImage(string? ImageKey, string? FallbackImageUrl);
}
