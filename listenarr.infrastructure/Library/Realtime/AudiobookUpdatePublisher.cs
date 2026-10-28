using AsyncKeyedLock;
using Listenarr.Application.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Realtime;

internal sealed class AudiobookUpdatePublisher(
    IServiceScopeFactory scopeFactory,
    IHubContext<DownloadHub> hubContext,
    ILogger<AudiobookUpdatePublisher> logger) : IAudiobookUpdatePublisher
{
    private readonly AsyncKeyedLocker<int> _publicationLocks = new();

    public async Task PublishCurrentAsync(
        int audiobookId,
        CancellationToken cancellationToken = default)
    {
        using var publicationLock = await _publicationLocks.LockAsync(
            audiobookId,
            cancellationToken);
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
        var audiobook = await repository.GetByIdAsync(audiobookId);
        if (audiobook == null)
        {
            logger.LogDebug(
                "Skipped AudiobookUpdate publication because audiobook {AudiobookId} no longer exists",
                audiobookId);
            return;
        }

        var dto = AudiobookDtoFactory.BuildFromEntity(audiobook);
        await hubContext.Clients.All.SendAsync(
            "AudiobookUpdate",
            dto,
            cancellationToken);
        logger.LogInformation(
            "Published current AudiobookUpdate for audiobook {AudiobookId}",
            audiobookId);
    }
}
