namespace Listenarr.Application.Downloads.Contracts;

/// <summary>
/// Executes the native slskd search, exact-file batch submission, and batch-status poll.
/// This is intentionally separate from torrent/NZB submission contracts.
/// </summary>
public interface ISlskdDownloadService
{
    Task<SlskdSubmissionResult> SearchSubmitAndPollAsync(
        DownloadClientConfiguration client,
        SlskdSubmissionRequest request,
        CancellationToken ct = default);
}

public sealed record SlskdSubmissionRequest(int AudiobookId, string SearchQuery, string? Title = null, string? Author = null);
public sealed record SlskdSubmissionResult(string BatchId, string Destination, IReadOnlyList<string> CompletedFiles, string? State);
