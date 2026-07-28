using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Slskd;

/// <summary>
/// Native slskd API integration. It deliberately submits only exact server-returned audio files;
/// it does not translate Soulseek responses into torrent or NZB payloads.
/// </summary>
public sealed class SlskdDownloadService : ISlskdDownloadService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SlskdDownloadService> _logger;
    private readonly IDownloadRepository _downloadRepository;
    private readonly SlskdSearchPollingOptions _searchPolling;

    public SlskdDownloadService(
        IHttpClientFactory httpClientFactory,
        ILogger<SlskdDownloadService> logger,
        IDownloadRepository downloadRepository,
        SlskdSearchPollingOptions? searchPolling = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _downloadRepository = downloadRepository;
        _searchPolling = searchPolling ?? SlskdSearchPollingOptions.Default;
        _searchPolling.Validate();
    }

    public async Task<SlskdSubmissionResult> SearchSubmitAndPollAsync(
        DownloadClientConfiguration client,
        SlskdSubmissionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(client.Type, "slskd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected download client is not an slskd client.", nameof(client));
        if (string.IsNullOrWhiteSpace(request.SearchQuery))
            throw new ArgumentException("A Soulseek search query is required.", nameof(request));

        var existing = await _downloadRepository.GetByAudiobookIdAsync(request.AudiobookId, ct);
        if (existing?.Any(download => download.Status is DownloadStatus.Queued
                or DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Completed
                or DownloadStatus.Processing or DownloadStatus.Ready or DownloadStatus.ImportPending
                or DownloadStatus.Moved || download.LastImportedAt.HasValue) == true)
            throw new DuplicateDownloadSubmissionException("An audiobook download is already active or imported.");

        var sourceRoot = SlskdRequestBuilder.GetListenarrVisibleSourceRoot(client);
        var tracked = new Download
        {
            AudiobookId = request.AudiobookId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? request.SearchQuery : request.Title,
            Artist = request.Author ?? string.Empty,
            Status = DownloadStatus.Queued,
            DownloadClientId = client.Id,
            DownloadPath = sourceRoot,
            StartedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>()
        };
        try
        {
            // Reserve the audiobook before any external side effect. The repository's unique
            // active-audiobook key is the cross-request concurrency gate.
            await _downloadRepository.AddAsync(tracked);
        }
        catch (UniqueConstraintViolationException ex)
        {
            throw new DuplicateDownloadSubmissionException("An active Listenarr download already exists for this audiobook.", ex);
        }

        var destinationKey = $"{request.AudiobookId.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{tracked.Id}";
        var destination = SlskdRequestBuilder.BuildDestination(destinationKey);
        using var http = _httpClientFactory.CreateClient(DownloadClientTypes.Slskd);
        http.BaseAddress = SlskdRequestBuilder.BuildBaseUri(client);
        SlskdRequestBuilder.ApplyOptionalApiKey(http, client);

        try
        {
            var search = await CreateSearchAsync(http, request.SearchQuery, ct);
            var selected = await GetFirstSafeResponseAsync(http, search.Id, ct);
            using var batchRequest = SlskdRequestBuilder.BuildBatchRequest(
                destinationKey,
                selected.Username,
                selected.Files);
            var batchResponse = await SendJsonAsync<SlskdBatchCreateResponse>(http, batchRequest, ct);
            var batch = batchResponse.Batch;
            if (string.IsNullOrWhiteSpace(batch?.Id))
                throw new DownloadClientSubmissionException("slskd did not return a batch identifier.");

            // Persist the native batch identifier as soon as Slskd accepts the batch so the
            // standard monitor/import pipeline owns the rest of the lifecycle.
            tracked.SetExternalId(batch.Id);
            await _downloadRepository.UpdateAsync(tracked);

            if (batchResponse.Failures is { Count: > 0 })
            {
                tracked.Failed("slskd accepted only part of the requested batch; manual reconciliation is required.");
                await _downloadRepository.UpdateAsync(tracked);
                throw new DownloadClientSubmissionException("slskd accepted only part of the requested batch; the partial batch was preserved for reconciliation.");
            }

            var polled = await PollBatchAsync(http, batch.Id, ct);
            var completedTransfers = polled.Transfers
                .Where(transfer => IsSuccessfulTerminal(transfer.State))
                .Select(transfer => new SlskdRemoteFile(transfer.Filename, transfer.Size));
            var completedFiles = SlskdRequestBuilder.MapCompletedFiles(sourceRoot, destination, completedTransfers);
            _logger.LogInformation("Submitted slskd batch {BatchId} for audiobook {AudiobookId}; mapped {FileCount} completed audio file(s)",
                LogRedaction.SanitizeText(batch.Id), request.AudiobookId, completedFiles.Count);
            var state = polled.Transfers.Count > 0 && completedFiles.Count == polled.Transfers.Count
                ? "Completed"
                : "Pending";
            return new SlskdSubmissionResult(batch.Id, destination, completedFiles, state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Fail only reservations that never acquired a client ID. Once accepted by Slskd,
            // keeping the active record prevents a retry from creating a second external batch.
            if (string.IsNullOrWhiteSpace(tracked.GetExternalId()))
            {
                tracked.Failed(ex.Message);
                await _downloadRepository.UpdateAsync(tracked);
            }

            throw;
        }
    }

    private static async Task<SlskdSearch> CreateSearchAsync(HttpClient http, string query, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/api/v0/searches", new { searchText = query.Trim() }, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        var search = await response.Content.ReadFromJsonAsync<SlskdSearch>(JsonOptions, ct);
        return search is { Id.Length: > 0 }
            ? search
            : throw new DownloadClientSubmissionException("slskd did not return a search identifier.");
    }

    private async Task<SlskdSearchResponse> GetFirstSafeResponseAsync(HttpClient http, string searchId, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_searchPolling.Timeout);
        using var pollingCt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var responseUri = $"/api/v0/searches/{Uri.EscapeDataString(searchId)}/responses";

        try
        {
            while (true)
            {
                using var response = await http.GetAsync(responseUri, pollingCt.Token);
                TimeSpan? retryAfter = null;
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    retryAfter = GetRetryAfter(response);
                    _logger.LogWarning("slskd rate-limited search {SearchId}; retrying within the {TimeoutSeconds}s search-result window", searchId, _searchPolling.Timeout.TotalSeconds);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    var responses = await response.Content.ReadFromJsonAsync<List<SlskdSearchResponse>>(JsonOptions, pollingCt.Token) ?? [];
                    // Prefer a peer that reports an immediately available upload slot. The
                    // former first-response policy could select a remotely queued source even
                    // when an equally safe source was available immediately.
                    var selected = responses
                        .Select(candidate => candidate with
                        {
                            Files = candidate.Files
                                .Where(file => SlskdRequestBuilder.IsSafeAudioFile(file.Filename, file.Size, file.Extension))
                                .GroupBy(file => file.Filename, StringComparer.OrdinalIgnoreCase)
                                .Select(group => group.First())
                                .ToList()
                        })
                        .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Username) && candidate.Files.Count > 0)
                        .OrderByDescending(candidate => candidate.HasFreeUploadSlot)
                        .ThenBy(candidate => candidate.Files.Count)
                        .ThenBy(candidate => candidate.QueueLength)
                        .FirstOrDefault();
                    if (selected is not null)
                        return selected;
                }

                var delay = retryAfter is { } rateLimitDelay && rateLimitDelay > TimeSpan.Zero
                    ? rateLimitDelay
                    : _searchPolling.PollInterval;
                await Task.Delay(delay, pollingCt.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new DownloadClientSubmissionException(
                $"slskd did not return a safe audio result for search '{searchId}' within {_searchPolling.Timeout.TotalSeconds:0} seconds. Verify the query or try again.");
        }
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        return retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
    }

    private static async Task<T> SendJsonAsync<T>(HttpClient http, HttpRequestMessage request, CancellationToken ct) where T : class
    {
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new DownloadClientSubmissionException("slskd returned an empty batch response.");
    }

    private static async Task<SlskdBatch> PollBatchAsync(HttpClient http, string batchId, CancellationToken ct)
    {
        // One explicit poll keeps request handling bounded; the normal Listenarr queue poller can invoke this workflow again.
        using var response = await http.GetAsync($"/api/v0/transfers/downloads/batches/{Uri.EscapeDataString(batchId)}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SlskdBatch>(JsonOptions, ct)
            ?? throw new DownloadClientSubmissionException("slskd returned an empty batch status response.");
    }

    private static bool IsSuccessfulTerminal(string? state)
    {
        var flags = (state ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return flags.Contains("Completed", StringComparer.OrdinalIgnoreCase) &&
               flags.Contains("Succeeded", StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record SlskdSearchPollingOptions(TimeSpan Timeout, TimeSpan PollInterval)
{
    public static SlskdSearchPollingOptions Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

    public void Validate()
    {
        if (Timeout <= TimeSpan.Zero || PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout), "slskd search polling timeout and interval must be positive.");
    }
}

public sealed record SlskdSearch(string Id);
public sealed record SlskdSearchResponse(
    string Username,
    List<SlskdRemoteFile> Files,
    bool HasFreeUploadSlot = false,
    int QueueLength = int.MaxValue);
public sealed record SlskdRemoteFile(string Filename, long Size, string? Extension = null);
public sealed record SlskdBatchCreateResponse(SlskdBatch? Batch, List<SlskdBatchFailure>? Failures);
public sealed record SlskdBatchFailure(string Filename, string Message);
public sealed record SlskdBatch(string Id, List<SlskdTransfer> Transfers, SlskdBatchOptions? Options = null);
public sealed record SlskdBatchOptions(string? Destination);
public sealed record SlskdTransfer(
    string Filename,
    long Size,
    string? State,
    long BytesTransferred = 0,
    double AverageSpeed = 0,
    string? Exception = null,
    bool Removed = false,
    string? Id = null,
    string? Username = null);
