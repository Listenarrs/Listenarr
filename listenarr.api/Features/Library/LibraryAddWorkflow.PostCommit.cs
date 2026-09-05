namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryAddWorkflow
    {
        private async Task SendAddedNotificationAsync(Audiobook audiobook)
        {
            if (_notificationService == null)
            {
                return;
            }

            try
            {
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
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to send post-commit Added notification for audiobook {AudiobookId}",
                    audiobook.Id);
            }
        }

        private async Task AddHistoryAsync(Audiobook audiobook)
        {
            try
            {
                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = audiobook.Id,
                    AudiobookTitle = audiobook.Title ?? "Unknown Title",
                    EventType = "Added",
                    Message = $"Audiobook '{audiobook.Title}' added to library from Add New page",
                    Source = "AddNew",
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write post-commit Added history for audiobook {AudiobookId}",
                    audiobook.Id);
            }
        }
    }
}
