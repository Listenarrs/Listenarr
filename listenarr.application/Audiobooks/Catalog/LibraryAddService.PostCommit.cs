using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Catalog;

public partial class LibraryAddService
{
    private async Task TryPublishPreparedImagesAsync(
        Audiobook audiobook,
        PreparedLibraryImage preparedImage,
        IReadOnlyList<string> preparedAuthorImages)
    {
        var fallbackImageUrl = audiobook.ImageUrl;
        try
        {
            var publishedImageUrl = await PublishLibraryImageAsync(preparedImage);
            if (!string.IsNullOrWhiteSpace(publishedImageUrl)
                && !string.Equals(
                    publishedImageUrl,
                    fallbackImageUrl,
                    StringComparison.Ordinal))
            {
                if (await _repo.TryUpdateImageUrlAsync(
                        audiobook.Id,
                        fallbackImageUrl,
                        publishedImageUrl,
                        CancellationToken.None))
                {
                    audiobook.ImageUrl = publishedImageUrl;
                }
                else
                {
                    _logger.LogWarning(
                        "Audiobook {AudiobookId} was added, but its published image URL could not be enrolled because the stored value changed",
                        audiobook.Id);
                }
            }
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Audiobook {AudiobookId} was added, but its prepared cover image could not be published",
                audiobook.Id);
        }

        foreach (var authorImageKey in preparedAuthorImages)
        {
            await PublishAuthorImageAsync(authorImageKey);
        }
    }

    private async Task TrySendAddedNotificationAsync(Audiobook audiobook)
    {
        try
        {
            await SendAddedNotificationAsync(audiobook);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
            && ex is not StackOverflowException)
        {
            _logger.LogWarning(
                ex,
                "Audiobook '{Title}' was added, but its notification could not be sent",
                audiobook.Title);
        }
    }

    private async Task SendAddedNotificationAsync(Audiobook audiobook)
    {
        if (_notificationService == null)
        {
            return;
        }

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
            NotificationTriggers.BookAdded,
            data);
    }

    private static History CreateHistoryEntry(
        Audiobook audiobook,
        LibraryAddOperationRequest request)
    {
        return new History
        {
            AudiobookId = audiobook.Id,
            AudiobookTitle = audiobook.Title ?? "Unknown Title",
            EventType = "Added",
            Message = request.HistoryMessage
                ?? $"Audiobook '{audiobook.Title}' added to library from {request.HistorySource}",
            Source = request.HistorySource,
            Timestamp = DateTime.UtcNow
        };
    }
}
