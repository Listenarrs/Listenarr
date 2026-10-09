namespace Listenarr.Infrastructure.Downloads.Cleanup;

public partial class MovedDownloadCleanupProcessor
{
    private static async Task<bool> CanReleaseClientOwnedSourceAsync(
        IDownloadClientGateway gateway,
        DownloadClientConfiguration client,
        Download download,
        CancellationToken cancellationToken)
    {
        // Stored completion/removability and age-based fallbacks cannot release
        // payloads retained for the download client's ownership.
        var liveItem = await gateway.GetQueueItemAsync(client, download, new QueueItem
        {
            Id = download.GetExternalId() ?? download.Id,
            DownloadClientId = client.Id
        }, cancellationToken);
        return liveItem.CanMoveFiles == true;
    }
}
