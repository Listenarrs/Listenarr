/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Application.Common;
using Listenarr.Application.Mapping;
using Listenarr.Domain.Common;
using Listenarr.Domain.Downloads.Exceptions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Common
{
    /// <summary>
    /// Responsibilities:
    /// - Make sure any path reported by any download client adapter is mapped using adequate Remote Path Mapping
    /// - Single point of contact for any download client adapter, no download client adapter detail should be visible behind this
    /// - Persistence: Do not persist anything here, it's up to callers to know what they are doing
    /// </summary>
    public class DownloadClientGateway(
        IRemotePathMappingService remotePathMappingService,
        IDownloadClientAdapterFactory factory,
        IFileSystem fileSystem,
        IFileSystemSemanticsResolver semanticsResolver,
        ILogger<DownloadClientGateway> logger) : IDownloadClientGateway
    {
        internal IDownloadClientAdapter ResolveAdapter(DownloadClientConfiguration client)
        {
            ArgumentNullException.ThrowIfNull(client);

            if (!string.IsNullOrWhiteSpace(client.Type))
            {
                try
                {
                    return factory.GetByType(client.Type);
                }
                catch (InvalidOperationException)
                {
                }
            }

            var descriptor = !string.IsNullOrWhiteSpace(client.Name)
                ? $"{client.Name} ({client.Type ?? "unknown"})"
                : client.Type ?? client.Id ?? "unknown";

            var message = $"No download client adapter registered for {LogRedaction.SanitizeText(descriptor)}.";
            logger.LogError(message);
            throw new InvalidOperationException(message);
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.TestConnectionAsync(client, ct);
        }

        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            if (!adapter.Protocols.Contains(submission.Protocol))
            {
                throw new DownloadClientSubmissionException(
                    $"Download client {client.Name ?? client.Type} does not support the prepared {submission.Protocol} submission.");
            }

            return await adapter.AddAsync(client, submission, ct);
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            // Removes the item from the external download client only. Database
            // removal belongs to the workflow that owns the durable state transition.
            var adapter = ResolveAdapter(client);
            return adapter.RemoveAsync(client, id, deleteFiles, ct);
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            var items = await adapter.GetQueueAsync(client, ct);
            var tasks = items.Select(item => TranslateQueueItemPathsAsync(client, item));
            return [.. await Task.WhenAll(tasks)];
        }

        public async Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, Download download, CancellationToken ct = default)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            if (download == null)
            {
                throw new ArgumentNullException(nameof(download));
            }

            var externalId = download.GetExternalId();
            if (string.IsNullOrEmpty(externalId))
            {
                return true;
            }

            if (!client.IsEnabled)
            {
                logger.LogDebug(
                    "Skipping mark imported for download {DownloadId}: download client {ClientId} is disabled",
                    download.Id,
                    client.Id);
                return true;
            }

            var adapter = ResolveAdapter(client);
            return await adapter.MarkItemAsImportedAsync(client, externalId, ct);
        }

        public async Task<QueueItem> GetQueueItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            var item = await adapter.GetImportItemAsync(client, download, queueItem, null, ct);

            return await TranslateQueueItemPathsAsync(client, item);
        }

        public async Task<List<Download>> FetchDownloadsAsync(DownloadClientConfiguration client, List<Download> downloads, CancellationToken ct = default)
        {
            var ids = GetExternalIds(downloads);
            if (ids.Count == 0)
            {
                return downloads;
            }

            var adapter = ResolveAdapter(client);
            List<QueueItem> items;
            try
            {
                items = await adapter.GetQueueAsync(client, ids!, ct);
            }
            catch (DownloadClientAdapterPollingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                throw new DownloadClientAdapterPollingException(
                    $"Error polling download client {client.Name ?? client.Id ?? client.Type}.",
                    ex);
            }

            var tasks = items.Select(item => TranslateQueueItemPathsAsync(client, item));
            items = [.. await Task.WhenAll(tasks)];

            foreach (QueueItem item in items)
            {
                var download = downloads.FirstOrDefault(d =>
                    string.Equals(d.GetExternalId(), item.Id, StringComparison.OrdinalIgnoreCase));
                if (download == null)
                {
                    continue;
                }

                logger.LogDebug(
                    "Found matching download client item for {DownloadId}: {Title} (ExternalId: {ExternalId}, Status: {Status}, Progress: {Progress:F2}, LocalPath: {LocalPath}, ContentPath: {ContentPath})",
                    download.Id,
                    item.Title,
                    item.Id,
                    item.Status,
                    item.Progress,
                    item.LocalPath,
                    item.ContentPath);

                var hasReliableSize = item.Size > 0 && item.Downloaded >= 0;
                var amountLeft = hasReliableSize
                    ? Math.Max(0, item.Size - item.Downloaded)
                    : null as long?;
                var normalizedState = (item.Status ?? string.Empty).ToLowerInvariant();
                var isExplicitCompletedState = normalizedState is "completed" or "success";

                logger.LogDebug(
                    "Completion diagnostic for {DownloadId}: Progress={Progress:F4}, HasReliableSize={HasReliableSize}, AmountLeft={AmountLeft}, ExplicitCompletedState={ExplicitCompletedState}, Status={Status}",
                    download.Id,
                    item.Progress,
                    hasReliableSize,
                    amountLeft,
                    isExplicitCompletedState,
                    item.Status);

                download = QueueItemConverter.UpdateFromQueueItem(download, item);
            }

            return downloads;
        }

        /// <summary>
        /// Give the list of external IDs from a list of download
        /// </summary>
        /// <param name="downloads"></param>
        /// <returns></returns>
        private List<string> GetExternalIds(List<Download> downloads)
        {
            return downloads
                .Select(d => d.GetExternalId())
                .Where(id => id != null)
                .ToHashSet()
                .ToList()!;
        }

        /// <summary>
        /// Handles path mapping of queue item
        /// Make sure all paths are locally accessible after processing and
        /// that a proper list of sanitized source files is produced
        /// </summary>
        /// <param name="client">Download client configuration to use for path mapping</param>
        /// <param name="item">Queue item to translate/sanitize</param>
        /// <returns></returns>
        private async Task<QueueItem> TranslateQueueItemPathsAsync(DownloadClientConfiguration client, QueueItem item)
        {
            if (!string.IsNullOrEmpty(item.RemotePath))
            {
                item.LocalPath = await remotePathMappingService.TranslatePathAsync(client, item.RemotePath);
                EnsureNativePath(item.LocalPath, client.Name);
            }

            if (!string.IsNullOrEmpty(item.ContentPath))
            {
                item.ContentPath = await remotePathMappingService.TranslatePathAsync(client, item.ContentPath);
                EnsureNativePath(item.ContentPath, client.Name);
            }

            // FIXME: https://github.com/Listenarrs/Listenarr/issues/592
            // Adapter ownership is still being clarified. Until that contract is tightened,
            // the gateway treats null and empty SourceFiles as "unknown" and derives a
            // file list only from a real ContentPath. Empty path strings are intentionally
            // ignored because some clients, especially active SABnzbd queue entries, do not
            // expose a completed storage path until history is available.
            if (item.SourceFiles != null && item.SourceFiles.Count > 0)
            {
                List<string> sourceFiles = [];
                foreach (string file in item.SourceFiles)
                {
                    var sourceFile = await remotePathMappingService.TranslatePathAsync(client, file);
                    EnsureNativePath(sourceFile, client.Name);
                    sourceFiles.Add(sourceFile);
                }
                item.SourceFiles = sourceFiles;
            }
            else if (!string.IsNullOrEmpty(item.ContentPath))
            {
                // Scan ContentPath only after the adapter has supplied a non-empty path.
                // Active queue snapshots may not be import-ready, so adapters should leave
                // ContentPath null until a reliable file or completed storage directory exists.
                if (fileSystem.FileExists(item.ContentPath))
                {
                    item.SourceFiles = [item.ContentPath];
                }
                else
                {
                    // Some clients can only report a directory. Expand it so import code can
                    // operate on the specific files that belong to this download.
                    try
                    {
                        item.SourceFiles = [.. fileSystem
                            .EnumerateFiles(item.ContentPath, "*.*", SearchOption.AllDirectories)
                            .Select(f => FileUtils.NormalizeStoredPath(f))];
                    }
                    catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                    {
                        LogMissingSourceFiles(
                            client,
                            item,
                            $"content path scanning failed with path {item.ContentPath}",
                            exception);
                        item.SourceFiles = [];
                    }
                }
            }
            else
            {
                if (IsImportSourceExpectedStatus(item.Status))
                {
                    LogMissingSourceFiles(client, item, "no content path", null);
                }

                item.SourceFiles = [];
            }

            // Source files represent local filesystem identities after remote-path mapping.
            // Use the reported local storage boundary rather than the host OS so Docker mounts
            // with explicit case behavior dedupe the same way the underlying volume does.
            var sourceFileComparer = await ResolveSourceFileComparerAsync(item);
            item.SourceFiles = new HashSet<string>(item.SourceFiles, sourceFileComparer).ToList();

            return item;
        }

        private async Task<IEqualityComparer<string>> ResolveSourceFileComparerAsync(QueueItem item)
        {
            var boundary = !string.IsNullOrWhiteSpace(item.ContentPath)
                ? item.ContentPath
                : item.SourceFiles?
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (string.IsNullOrWhiteSpace(boundary))
            {
                return StringComparer.Ordinal;
            }

            try
            {
                var resolution = await semanticsResolver.ResolveAsync(boundary);
                return resolution.State == PathIdentityState.Valid
                    ? resolution.Semantics.Comparer
                    : StringComparer.Ordinal;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogDebug(
                    exception,
                    "Failed to resolve download source file semantics for {Path}",
                    LogRedaction.SanitizeFilePath(boundary));
                return StringComparer.Ordinal;
            }
        }

        private static void EnsureNativePath(string? path, string clientName)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    path,
                    out _,
                    out _))
            {
                throw new InvalidOperationException(
                    $"Download client '{clientName}' reported a save path that is not valid on this host; check its remote path mappings.");
            }
        }

        private void LogMissingSourceFiles(
            DownloadClientConfiguration client,
            QueueItem item,
            string reason,
            Exception? exception)
        {
            if (!IsImportSourceExpectedStatus(item.Status))
            {
                return;
            }

            logger.LogWarning(
                exception,
                "Download client {ClientId} reported no source files and {Reason} for item {Title}",
                client.Id,
                reason,
                item.Title);
        }

        internal static bool IsImportSourceExpectedStatus(string? status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "processing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "importpending", StringComparison.OrdinalIgnoreCase);
        }
    }
}
