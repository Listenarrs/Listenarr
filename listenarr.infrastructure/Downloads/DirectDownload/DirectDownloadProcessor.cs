/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.DirectDownload;

internal sealed class DirectDownloadProcessor(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IEnumerable<IDirectDownloadSourcePolicy> sourcePolicies,
    IApplicationPathService applicationPathService,
    ILogger<DirectDownloadProcessor> logger) : IDirectDownloadProcessor
{
    private const string DirectDownloadClientName = "DirectDownload";
    private static readonly TimeSpan ProgressPersistInterval = TimeSpan.FromSeconds(5);
    private const long ProgressPersistBytes = 5 * 1024 * 1024;
    private const int MaxArtifactCount = 500;

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

        string? stagingRoot = null;
        try
        {
            if (!TryResolveSourcePolicy(download, out var policy, out var policyError))
            {
                await MarkFailedAsync(downloadRepository, download, policyError, cancellationToken);
                return;
            }

            if (!TryResolveArtifacts(download, policy, out var artifacts, out var validationError))
            {
                await MarkFailedAsync(downloadRepository, download, validationError, cancellationToken);
                return;
            }

            stagingRoot = BuildStagingRoot(download);
            var transfers = artifacts
                .Select(artifact => new DirectDownloadTransfer(
                    artifact,
                    Path.Combine(stagingRoot, artifact.FileName)))
                .ToList();
            Directory.CreateDirectory(stagingRoot);

            download.Downloading();
            download.DownloadPath = transfers.Count == 1
                ? transfers[0].FinalPath
                : stagingRoot;
            var expectedTotal = artifacts.Sum(artifact => artifact.ExpectedSize);
            if (expectedTotal > 0)
            {
                download.TotalSize = Math.Max(download.TotalSize, expectedTotal);
            }
            download.SetMetadata(DirectDownloadMetadataKeys.StartedAt, DateTime.UtcNow.ToString("O"));
            await downloadRepository.UpdateAsync(download);

            logger.LogInformation(
                "Downloading direct-download item {DownloadId} with {ArtifactCount} artifact(s)",
                download.Id,
                transfers.Count);

            var client = httpClientFactory.CreateClient(DirectDownloadClientName);
            long downloadedBytes = 0;
            long lastPersistedBytes = 0;
            var lastPersistedAt = DateTime.UtcNow;

            foreach (var transfer in transfers)
            {
                var partialPath = transfer.FinalPath + ".partial";
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }

                using var response = await GetTrustedResponseAsync(
                    client,
                    policy,
                    transfer.Artifact.DownloadUri,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength is long knownLength && knownLength > 0)
                {
                    if (transfer.Artifact.ExpectedSize > 0 && knownLength != transfer.Artifact.ExpectedSize)
                    {
                        throw new IOException(
                            $"Direct-download artifact {transfer.Artifact.FileName} expected {transfer.Artifact.ExpectedSize} bytes but the response advertised {knownLength} bytes.");
                    }

                    if (transfer.Artifact.ExpectedSize == 0)
                    {
                        download.TotalSize = Math.Max(download.TotalSize, downloadedBytes + knownLength);
                        await downloadRepository.UpdateAsync(download);
                    }
                }

                var expectedArtifactBytes = transfer.Artifact.ExpectedSize > 0
                    ? transfer.Artifact.ExpectedSize
                    : contentLength.GetValueOrDefault();
                long artifactBytesWritten = 0;
                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var fileStream = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        var bytesRead = await responseStream.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        await fileStream.WriteAsync(
                            buffer.AsMemory(0, bytesRead),
                            cancellationToken);
                        artifactBytesWritten += bytesRead;
                        downloadedBytes += bytesRead;

                        if (ShouldPersistProgress(downloadedBytes, lastPersistedBytes, lastPersistedAt))
                        {
                            ApplyProgress(download, downloadedBytes);
                            await downloadRepository.UpdateAsync(download);
                            lastPersistedBytes = downloadedBytes;
                            lastPersistedAt = DateTime.UtcNow;
                        }
                    }

                    await fileStream.FlushAsync(cancellationToken);
                }

                if (expectedArtifactBytes > 0 && artifactBytesWritten != expectedArtifactBytes)
                {
                    throw new IOException(
                        $"Direct-download artifact {transfer.Artifact.FileName} expected {expectedArtifactBytes} bytes but downloaded {artifactBytesWritten} bytes.");
                }

                File.Move(partialPath, transfer.FinalPath, overwrite: true);
            }

            download.TotalSize = Math.Max(download.TotalSize, downloadedBytes);
            ApplyProgress(download, downloadedBytes);
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
            if (!string.IsNullOrWhiteSpace(stagingRoot) && Directory.Exists(stagingRoot))
            {
                TryDeleteStagingDirectory(stagingRoot);
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
            await EnsureResolvedExternalTargetAsync(currentUri);

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

            await EnsureResolvedExternalTargetAsync(redirectUri);
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
        string.Equals(download.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase) &&
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

    private static bool TryResolveArtifacts(
        Download download,
        IDirectDownloadSourcePolicy policy,
        out IReadOnlyList<ResolvedDirectDownloadArtifact> artifacts,
        out string error)
    {
        var persistedPlan = download.GetMetadataString(DirectDownloadMetadataKeys.ArtifactPlan);
        List<PersistedDirectDownloadArtifact> plan;
        if (string.IsNullOrWhiteSpace(persistedPlan))
        {
            plan =
            [
                new PersistedDirectDownloadArtifact(
                    download.OriginalUrl,
                    string.Empty,
                    Math.Max(0, download.TotalSize),
                    DirectDownloadArtifactPackaging.File)
            ];
        }
        else
        {
            try
            {
                var artifactPlan = JsonSerializer.Deserialize<PersistedDirectDownloadArtifactPlan>(persistedPlan);
                if (artifactPlan?.Version != PersistedDirectDownloadArtifactPlan.CurrentVersion ||
                    artifactPlan.Artifacts == null)
                {
                    artifacts = [];
                    error = "The direct-download artifact plan version is invalid or unsupported.";
                    return false;
                }

                plan = [.. artifactPlan.Artifacts];
            }
            catch (JsonException)
            {
                artifacts = [];
                error = "The direct-download artifact plan is invalid.";
                return false;
            }
        }

        if (plan.Count == 0 || plan.Count > MaxArtifactCount)
        {
            artifacts = [];
            error = $"The direct-download artifact plan must contain between 1 and {MaxArtifactCount} files.";
            return false;
        }

        var resolved = new List<ResolvedDirectDownloadArtifact>(plan.Count);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        foreach (var item in plan)
        {
            if (item.ExpectedSize < 0 || !Enum.IsDefined(item.Packaging))
            {
                artifacts = [];
                error = "The direct-download artifact metadata is invalid.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.Url) ||
                !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ||
                !policy.TryValidateInitialUri(uri, out error))
            {
                artifacts = [];
                error = string.IsNullOrWhiteSpace(error)
                    ? "The direct-download URL is invalid."
                    : error;
                return false;
            }

            var sourceFileName = string.IsNullOrWhiteSpace(item.FileName)
                ? policy.GetFileName(uri, download)
                : item.FileName;
            if (!DirectDownloadArtifactFileNames.TryNormalizeArtifactFileName(
                sourceFileName,
                out var fileName,
                out error))
            {
                artifacts = [];
                return false;
            }

            if (!fileNames.Add(fileName))
            {
                artifacts = [];
                error = "The direct-download artifact plan contains duplicate filenames.";
                return false;
            }

            resolved.Add(new ResolvedDirectDownloadArtifact(
                uri,
                fileName,
                Math.Max(0, item.ExpectedSize),
                item.Packaging));
        }

        if (!policy.TryValidateArtifactPlan(resolved.Select(artifact => artifact.DownloadUri).ToList(), out error))
        {
            artifacts = [];
            return false;
        }

        artifacts = resolved;
        error = string.Empty;
        return true;
    }

    private string BuildStagingRoot(Download download) =>
        applicationPathService.ResolveFromConfig(
            "downloads",
            "direct",
            DirectDownloadArtifactFileNames.SanitizePathSegment(download.Id));

    private static bool ShouldPersistProgress(
        long downloadedBytes,
        long lastPersistedBytes,
        DateTime lastPersistedAt) =>
        downloadedBytes - lastPersistedBytes >= ProgressPersistBytes ||
        DateTime.UtcNow - lastPersistedAt >= ProgressPersistInterval;

    private static void ApplyProgress(Download download, long downloadedBytes)
    {
        download.DownloadedSize = downloadedBytes;
        if (download.TotalSize > 0)
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

    private async Task EnsureResolvedExternalTargetAsync(Uri uri)
    {
        // Source policies validate source-specific hosts and paths. The worker
        // still performs resolved-network validation before every request so a
        // future policy cannot accidentally allow private or loopback targets.
        if (!await OutboundRequestSecurity.TryValidateResolvedExternalHttpUriAsync(
            uri,
            logger,
            allowPrivateTargets: false))
        {
            throw new HttpRequestException("Direct download target resolved to a private or loopback address.");
        }
    }

    private static void TryDeleteStagingDirectory(string stagingRoot)
    {
        try
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
        catch
        {
            // Best effort only. A retry replaces partial files before transfer.
        }
    }

    private sealed record ResolvedDirectDownloadArtifact(
        Uri DownloadUri,
        string FileName,
        long ExpectedSize,
        DirectDownloadArtifactPackaging Packaging);

    private sealed record DirectDownloadTransfer(
        ResolvedDirectDownloadArtifact Artifact,
        string FinalPath);
}
