using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Slskd;

/// <summary>
/// Polling/import adapter for batches created by the native Slskd submission service.
/// It never submits generic torrent/NZB payloads and removes an isolated native batch
/// only after the framework has proved its canonical import succeeded.
/// </summary>
public sealed class SlskdAdapter : IDownloadClientAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SlskdAdapter> _logger;
    private readonly IFileSystem _fileSystem;

    public SlskdAdapter(IHttpClientFactory httpClientFactory, ILogger<SlskdAdapter> logger, IFileSystem fileSystem)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _fileSystem = fileSystem;
    }

    public string ClientType => DownloadClientTypes.Slskd;
    public DownloadProtocol Protocol => DownloadProtocol.Unknown;

    public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient(client);
            using var response = await http.GetAsync("/api/v0/application", ct);
            return response.IsSuccessStatusCode
                ? (true, "Connected to slskd")
                : (false, $"slskd returned HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Unable to test native slskd connection for client {ClientId}", LogRedaction.SanitizeText(client.Id));
            return (false, "Unable to connect to slskd");
        }
    }

    public Task<DownloadClientSubmissionResult> AddAsync(DownloadClientConfiguration client, PreparedDownloadSubmission submission, CancellationToken ct = default)
        => throw new DownloadClientSubmissionException("Native slskd submissions must use the Listenarr Slskd search-and-download workflow.");

    public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient(client);
            var batch = await GetBatchAsync(http, id, ct);
            if (batch is null) return true;

            var transfers = (batch.Transfers ?? []).Where(transfer => !transfer.Removed).ToList();
            if (transfers.Any(transfer => string.IsNullOrWhiteSpace(transfer.Id) || string.IsNullOrWhiteSpace(transfer.Username)))
            {
                _logger.LogWarning("Unable to remove native slskd batch {BatchId}: one or more transfers have no id or username", LogRedaction.SanitizeText(id));
                return false;
            }

            if (deleteFiles)
            {
                if (transfers.Count == 0 || transfers.Any(transfer => !IsSucceeded(transfer.State)))
                {
                    _logger.LogWarning("Refusing destructive cleanup of native slskd batch {BatchId}: not every transfer succeeded", LogRedaction.SanitizeText(id));
                    return false;
                }

                var stageDirectory = GetStageDirectory(client, batch);
                if (stageDirectory is null)
                {
                    _logger.LogWarning("Refusing destructive cleanup of native slskd batch {BatchId}: the isolated stage path is unsafe", LogRedaction.SanitizeText(id));
                    return false;
                }

                if (_fileSystem.DirectoryExists(stageDirectory))
                {
                    var actualFiles = _fileSystem.EnumerateFiles(stageDirectory).ToList();
                    var stageFiles = actualFiles.Count == 0
                        ? actualFiles
                        : ResolveCompletedStageFiles(client, batch, transfers);
                    if (stageFiles is null)
                    {
                        _logger.LogWarning("Refusing destructive cleanup of native slskd batch {BatchId}: non-empty stage content could not be verified", LogRedaction.SanitizeText(id));
                        return false;
                    }

                    foreach (var file in stageFiles)
                    {
                        if (!_fileSystem.TryValidateMutationTarget(file, [stageDirectory], out var safeFile, out var reason))
                        {
                            _logger.LogWarning("Refusing destructive cleanup of native slskd batch {BatchId}: {Reason}", LogRedaction.SanitizeText(id), LogRedaction.SanitizeText(reason));
                            return false;
                        }
                        if (_fileSystem.FileExists(file)) _fileSystem.DeleteFile(file);
                    }
                    _fileSystem.DeleteEmptyDirectories(stageDirectory);
                }
            }

            // slskd exposes transfer removal rather than batch removal. Remove every transfer
            // in this one batch; legitimate chapter files remain distinct operations.
            foreach (var transfer in transfers)
            {
                var uri = $"/api/v0/transfers/downloads/{Uri.EscapeDataString(transfer.Username!)}/{Uri.EscapeDataString(transfer.Id!)}?remove=true";
                using var response = await http.DeleteAsync(uri, ct);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Unable to remove transfer {TransferId} from native slskd batch {BatchId}: HTTP {StatusCode}",
                        LogRedaction.SanitizeText(transfer.Id), LogRedaction.SanitizeText(id), (int)response.StatusCode);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Unable to remove native slskd batch {BatchId}", LogRedaction.SanitizeText(id));
            return false;
        }
    }

    public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient(client);
            using var response = await http.GetAsync("/api/v0/transfers/downloads", ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var batchIds = FindBatchIds(document.RootElement).Distinct(StringComparer.OrdinalIgnoreCase);
            var items = new List<QueueItem>();
            foreach (var batchId in batchIds)
            {
                var batch = await GetBatchAsync(http, batchId, ct);
                if (batch is not null)
                {
                    items.Add(MapQueueItem(client, batch));
                }
            }

            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Unable to fetch native slskd queue snapshot for client {ClientId}", LogRedaction.SanitizeText(client.Id));
            throw new DownloadClientAdapterPollingException("Error fetching native slskd queue snapshot.", ex);
        }
    }

    public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var items = new List<QueueItem>();
        try
        {
            using var http = CreateClient(client);
            foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var batch = await GetBatchAsync(http, id, ct);
                if (batch is not null) items.Add(MapQueueItem(client, batch));
            }
            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Unable to poll native slskd batch(es) for client {ClientId}", LogRedaction.SanitizeText(client.Id));
            throw new DownloadClientAdapterPollingException("Error polling native slskd batch status.", ex);
        }
    }

    public Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        => Task.FromResult(new List<DownloadClientItem>());

    public async Task<QueueItem> GetImportItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, QueueItem? previousAttempt = null, CancellationToken ct = default)
    {
        var batchId = download.GetExternalId() ?? queueItem.Id;
        if (string.IsNullOrWhiteSpace(batchId)) return queueItem;
        using var http = CreateClient(client);
        var batch = await GetBatchAsync(http, batchId, ct);
        return batch is null ? queueItem : MapQueueItem(client, batch);
    }

    public Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string id, CancellationToken ct = default)
        // Called by the framework only after every canonical import result succeeded.
        // This client destination is staging, not user-retained download data.
        => RemoveAsync(client, id, deleteFiles: true, ct);

    private HttpClient CreateClient(DownloadClientConfiguration client)
    {
        var http = _httpClientFactory.CreateClient(DownloadClientTypes.Slskd);
        http.BaseAddress = SlskdRequestBuilder.BuildBaseUri(client);
        SlskdRequestBuilder.ApplyOptionalApiKey(http, client);
        return http;
    }

    private static async Task<SlskdBatch?> GetBatchAsync(HttpClient http, string batchId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/api/v0/transfers/downloads/batches/{Uri.EscapeDataString(batchId)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SlskdBatch>(JsonOptions, ct);
    }

    private QueueItem MapQueueItem(DownloadClientConfiguration client, SlskdBatch batch)
    {
        var transfers = (batch.Transfers ?? []).Where(t => !t.Removed).ToList();
        var total = transfers.Sum(t => Math.Max(0, t.Size));
        var downloaded = transfers.Sum(t => Math.Min(Math.Max(0, t.Size), Math.Max(0, t.BytesTransferred)));
        var allSucceeded = transfers.Count > 0 && transfers.All(t => IsSucceeded(t.State));
        var failure = transfers.FirstOrDefault(t => IsFailure(t.State));
        var destination = batch.Options?.Destination ?? string.Empty;
        var sourceFiles = allSucceeded ? ResolveCompletedStageFiles(client, batch, transfers) : null;
        var hasSafeCompletionPaths = allSucceeded && sourceFiles is { Count: > 0 } && sourceFiles.Count == transfers.Count;
        var contentPath = hasSafeCompletionPaths
            ? Path.GetDirectoryName(sourceFiles![0])
            : null;
        var state = failure is not null || (allSucceeded && !hasSafeCompletionPaths)
            ? "failed"
            : allSucceeded
                ? "completed"
                : transfers.All(t => IsQueued(t.State))
                    ? "queued"
                    : "downloading";
        return new QueueItem
        {
            Id = batch.Id,
            Title = destination,
            Status = state,
            Progress = total > 0 ? Math.Min(100d, downloaded * 100d / total) : 0d,
            Size = total,
            Downloaded = downloaded,
            DownloadSpeed = transfers.Sum(t => Math.Max(0, t.AverageSpeed)),
            DownloadClient = client.Name ?? DownloadClientTypes.Slskd,
            DownloadClientId = client.Id,
            DownloadClientType = DownloadClientTypes.Slskd,
            RemotePath = contentPath,
            ContentPath = contentPath,
            SourceFiles = hasSafeCompletionPaths ? sourceFiles : null,
            CompletionTime = hasSafeCompletionPaths ? DateTime.UtcNow : null,
            CanPause = false,
            CanRemove = allSucceeded,
            ErrorMessage = failure?.Exception ?? (allSucceeded && !hasSafeCompletionPaths ? "slskd reported an unsafe or incomplete completion path." : null),
            ClientFailureReason = failure?.Exception ?? (allSucceeded && !hasSafeCompletionPaths ? "slskd reported an unsafe or incomplete completion path." : null)
        };
    }

    private string? GetStageDirectory(DownloadClientConfiguration client, SlskdBatch batch)
    {
        var mapped = SlskdRequestBuilder.MapCompletedFiles(
            SlskdRequestBuilder.GetListenarrVisibleSourceRoot(client),
            batch.Options?.Destination ?? string.Empty,
            [new SlskdRemoteFile("stage.m4b", 1)]);
        return mapped.Count == 1 ? Path.GetDirectoryName(mapped[0]) : null;
    }

    private List<string>? ResolveCompletedStageFiles(
        DownloadClientConfiguration client,
        SlskdBatch batch,
        IReadOnlyCollection<SlskdTransfer> transfers)
    {
        if (transfers.Count == 0 || transfers.Any(t => !IsSucceeded(t.State))) return null;
        var stageDirectory = GetStageDirectory(client, batch);
        if (stageDirectory is null || !_fileSystem.DirectoryExists(stageDirectory)) return null;

        var actual = _fileSystem.EnumerateFiles(stageDirectory).ToList();
        if (actual.Count != transfers.Count) return null;
        var remaining = new List<string>(actual);
        foreach (var transfer in transfers)
        {
            var expectedName = Path.GetFileName(transfer.Filename.Replace('\\', '/'));
            var expectedStem = Path.GetFileNameWithoutExtension(expectedName);
            var expectedExtension = Path.GetExtension(expectedName);
            var match = remaining.FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase) &&
                _fileSystem.GetFileLength(path) == transfer.Size);
            match ??= remaining.FirstOrDefault(path =>
                _fileSystem.GetFileLength(path) == transfer.Size &&
                string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase) &&
                IsCollisionRename(Path.GetFileNameWithoutExtension(path), expectedStem));
            if (match is null) return null;
            remaining.Remove(match);
        }
        return actual;
    }

    private static bool IsCollisionRename(string actualStem, string expectedStem)
    {
        if (!actualStem.StartsWith(expectedStem + "_", StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = actualStem[(expectedStem.Length + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
    }

    private static IEnumerable<string> FindBatchIds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "batchId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(property.Value.GetString(), out var batchId))
                {
                    yield return batchId.ToString();
                }
                else
                {
                    foreach (var id in FindBatchIds(property.Value)) yield return id;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                foreach (var id in FindBatchIds(child)) yield return id;
            }
        }
    }

    private static bool HasStateFlag(string? state, string flag) => state?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Any(value => string.Equals(value, flag, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool IsSucceeded(string? state) => HasStateFlag(state, "Completed") && HasStateFlag(state, "Succeeded");

    private static bool IsFailure(string? state) => HasStateFlag(state, "Completed") &&
        (HasStateFlag(state, "Cancelled") || HasStateFlag(state, "TimedOut") || HasStateFlag(state, "Errored") || HasStateFlag(state, "Rejected") || HasStateFlag(state, "Aborted"));

    private static bool IsQueued(string? state) => HasStateFlag(state, "Requested") || HasStateFlag(state, "Queued");
}
