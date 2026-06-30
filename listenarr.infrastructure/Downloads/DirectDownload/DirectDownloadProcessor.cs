// Listenarr - Audiobook Management System
// Copyright (C) 2024-2026 Listenarr Contributors

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.DirectDownload;

public sealed class DirectDownloadProcessor(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IEnumerable<IDirectDownloadSourcePolicy> sourcePolicies,
    ILogger<DirectDownloadProcessor> logger) : IDirectDownloadProcessor
{
    private const string DirectDownloadClientName = "DirectDownload";
    private static readonly TimeSpan ProgressPersistInterval = TimeSpan.FromSeconds(5);
    private const long ProgressPersistBytes = 5 * 1024 * 1024;

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
        var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
        var settings = await configurationService.GetApplicationSettingsAsync();
        var maxDownloads = Math.Max(1, settings.MaxConcurrentDownloads);

        var candidates = (await downloadRepository.GetActiveAsync())
            .Where(IsActiveDirectDownload)
            .OrderBy(download => download.StartedAt)
            .Take(maxDownloads)
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(candidates.Select(download =>
            ProcessDownloadAsync(download, cancellationToken)));
    }

    public async Task ProcessDownloadAsync(Download download, CancellationToken cancellationToken)
    {
        if (!IsActiveDirectDownload(download))
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
        var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();

        string? partialPath = null;
        try
        {
            if (!TryResolveSourcePolicy(download, out var policy, out var policyError))
            {
                await MarkFailedAsync(downloadRepository, download, policyError, cancellationToken);
                return;
            }

            if (!TryValidateDirectDownloadUri(download.OriginalUrl, policy, out var downloadUri, out var validationError))
            {
                await MarkFailedAsync(downloadRepository, download, validationError, cancellationToken);
                return;
            }

            var finalPath = BuildStagingFilePath(download, downloadUri, policy);
            partialPath = finalPath + ".partial";
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            download.Downloading();
            download.DownloadPath = finalPath;
            download.SetMetadata(DirectDownloadMetadataKeys.StartedAt, DateTime.UtcNow.ToString("O"));
            await downloadRepository.UpdateAsync(download);

            logger.LogInformation(
                "Downloading direct-download item {DownloadId} from {Host} to {Path}",
                download.Id,
                downloadUri.Host,
                LogRedaction.SanitizeFilePath(finalPath));

            var client = httpClientFactory.CreateClient(DirectDownloadClientName);
            using var response = await GetTrustedResponseAsync(client, policy, downloadUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is long knownTotalBytes && knownTotalBytes > 0)
            {
                download.TotalSize = Math.Max(download.TotalSize, knownTotalBytes);
                await downloadRepository.UpdateAsync(download);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            long downloadedBytes = 0;
            long lastPersistedBytes = 0;
            var lastPersistedAt = DateTime.UtcNow;

            while (true)
            {
                var bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloadedBytes += bytesRead;

                if (ShouldPersistProgress(downloadedBytes, lastPersistedBytes, lastPersistedAt))
                {
                    ApplyProgress(download, downloadedBytes, totalBytes);
                    await downloadRepository.UpdateAsync(download);
                    lastPersistedBytes = downloadedBytes;
                    lastPersistedAt = DateTime.UtcNow;
                }
            }

            await fileStream.FlushAsync(cancellationToken);
            await fileStream.DisposeAsync();

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }
            File.Move(partialPath, finalPath);
            partialPath = null;

            ApplyProgress(download, downloadedBytes, totalBytes ?? downloadedBytes);
            download.CompletedAt = DateTime.UtcNow;
            download.Completed();
            download.SetMetadata(DirectDownloadMetadataKeys.CompletedAt, DateTime.UtcNow.ToString("O"));
            await downloadRepository.UpdateAsync(download);
            await downloadProcessingJobService.EnqueueAsync(download);

            logger.LogInformation(
                "Direct-download item {DownloadId} completed and was queued for import",
                download.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Direct-download processing canceled for {DownloadId}", download.Id);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException && exception is not StackOverflowException)
        {
            if (!string.IsNullOrWhiteSpace(partialPath) && File.Exists(partialPath))
            {
                TryDeletePartialFile(partialPath);
            }

            await MarkFailedAsync(downloadRepository, download, $"Direct download failed: {exception.Message}", cancellationToken);
            logger.LogWarning(exception, "Direct-download item {DownloadId} failed", download.Id);
        }
    }

    private async Task<HttpResponseMessage> GetTrustedResponseAsync(
        HttpClient client,
        IDirectDownloadSourcePolicy policy,
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            var response = await client.GetAsync(
                currentUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location == null)
            {
                throw new HttpRequestException("Direct download returned a redirect without a Location header.");
            }

            var redirectUri = location.IsAbsoluteUri
                ? location
                : new Uri(currentUri, location);

            // Redirect validation is delegated to the selected source policy. This
            // keeps the transfer worker generic while preventing open-redirect abuse.
            if (!policy.TryValidateRedirectUri(redirectUri, currentUri, out var redirectValidationError))
            {
                throw new HttpRequestException($"Direct download redirect was rejected: {redirectValidationError}");
            }

            currentUri = redirectUri;
        }

        throw new HttpRequestException("Direct download exceeded the maximum redirect count.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsActiveDirectDownload(Download download) =>
        string.Equals(download.DownloadClientId, "DDL", StringComparison.OrdinalIgnoreCase) &&
        download.Status is DownloadStatus.Queued or DownloadStatus.Downloading;

    private bool TryResolveSourcePolicy(
        Download download,
        out IDirectDownloadSourcePolicy policy,
        out string error)
    {
        var policyKey = download.GetMetadataString(DirectDownloadMetadataKeys.SourcePolicyKey);
        if (string.IsNullOrWhiteSpace(policyKey))
        {
            error = "The direct-download source policy is missing or unsupported.";
            policy = null!;
            return false;
        }

        policy = sourcePolicies.FirstOrDefault(policy =>
            string.Equals(policy.Key, policyKey, StringComparison.OrdinalIgnoreCase))!;
        if (policy == null)
        {
            error = "The direct-download source policy is missing or unsupported.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateDirectDownloadUri(
        string value,
        IDirectDownloadSourcePolicy policy,
        out Uri uri,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out uri!))
        {
            error = "The direct-download URL is invalid.";
            uri = new Uri("about:blank");
            return false;
        }

        return policy.TryValidateInitialUri(uri, out error);
    }

    private static string BuildStagingFilePath(
        Download download,
        Uri downloadUri,
        IDirectDownloadSourcePolicy policy)
    {
        var fileName = SanitizeFileName(policy.GetFileName(downloadUri, download));
        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            fileName += ".download";
        }

        return Path.Combine(
            Path.GetTempPath(),
            "listenarr-direct-downloads",
            SanitizeFileName(download.Id),
            fileName);
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character =>
            invalidChars.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    private static bool ShouldPersistProgress(
        long downloadedBytes,
        long lastPersistedBytes,
        DateTime lastPersistedAt) =>
        downloadedBytes - lastPersistedBytes >= ProgressPersistBytes ||
        DateTime.UtcNow - lastPersistedAt >= ProgressPersistInterval;

    private static void ApplyProgress(Download download, long downloadedBytes, long? totalBytes)
    {
        download.DownloadedSize = downloadedBytes;
        if (totalBytes.GetValueOrDefault() > 0)
        {
            download.TotalSize = Math.Max(download.TotalSize, totalBytes!.Value);
            download.Progress = Math.Min(99, Math.Round(downloadedBytes * 100M / totalBytes.Value, 2));
        }
        else if (download.TotalSize > 0)
        {
            download.Progress = Math.Min(99, Math.Round(downloadedBytes * 100M / download.TotalSize, 2));
        }
    }

    private static async Task MarkFailedAsync(
        IDownloadRepository downloadRepository,
        Download download,
        string reason,
        CancellationToken cancellationToken)
    {
        download.Failed(reason);
        download.SetMetadata(DirectDownloadMetadataKeys.FailedAt, DateTime.UtcNow.ToString("O"));
        await downloadRepository.UpdateAsync(download);
    }

    private static void TryDeletePartialFile(string partialPath)
    {
        try
        {
            File.Delete(partialPath);
        }
        catch
        {
            // Best effort only. The next retry starts by replacing this partial file.
        }
    }
}
