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
using System.Security.Cryptography;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private const int FileMoveLockStripeCount = 4096;
    private static readonly object FileMoveGateRegistryLock = new();
    private static readonly Dictionary<string, FileMoveGateEntry> FileMoveGates = [];

    private sealed class FileMoveGateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int UserCount { get; set; }
    }

    private sealed class FileMoveGateLease(
        string key,
        FileMoveGateEntry entry,
        IReadOnlyList<FileStream> stripeLocks,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? lockDirectory,
        FileMoveEndpoint source,
        FileMoveEndpoint destination,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? sourceParent,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? destinationParent) : IDisposable
    {
        private FileMoveGateEntry? _entry = entry;

        public string SourceIdentity { get; } = source.LockIdentity;
        public string DestinationIdentity { get; } = destination.LockIdentity;
        public string SourcePath { get; } = source.ResolvedPath;
        public string DestinationPath { get; } = destination.ResolvedPath;
        public string SourceName { get; } = Path.GetFileName(source.ResolvedPath);
        public string DestinationName { get; } = Path.GetFileName(destination.ResolvedPath);
        private readonly PinnedDirectoryCreation.PinnedDirectoryAnchor? _sourceParent =
            sourceParent;
        private readonly PinnedDirectoryCreation.PinnedDirectoryAnchor? _destinationParent =
            destinationParent;
        public PinnedDirectoryCreation.PinnedDirectoryAnchor SourceParent =>
            _sourceParent ?? throw new InvalidOperationException(
                "The file-move source parent was not pinned.");
        public PinnedDirectoryCreation.PinnedDirectoryAnchor DestinationParent =>
            _destinationParent ?? throw new InvalidOperationException(
                "The file-move destination parent was not pinned.");
        private IReadOnlyList<FileStream>? _stripeLocks = stripeLocks;
        private PinnedDirectoryCreation.PinnedDirectoryAnchor? _lockDirectory =
            lockDirectory;

        public void Dispose()
        {
            var releasedEntry = Interlocked.Exchange(ref _entry, null);
            if (releasedEntry == null)
            {
                return;
            }
            _sourceParent?.Dispose();
            _destinationParent?.Dispose();

            var locks = Interlocked.Exchange(ref _stripeLocks, null);
            if (locks != null)
            {
                foreach (var stripeLock in locks.Reverse())
                {
                    stripeLock.Dispose();
                }
            }
            Interlocked.Exchange(ref _lockDirectory, null)?.Dispose();

            releasedEntry.Semaphore.Release();
            lock (FileMoveGateRegistryLock)
            {
                releasedEntry.UserCount--;
                if (releasedEntry.UserCount == 0)
                {
                    FileMoveGates.Remove(key);
                    releasedEntry.Semaphore.Dispose();
                }
            }
        }
    }

    private sealed record FileMoveEndpoint(string LockIdentity, string ResolvedPath);

    private async Task<FileMoveGateLease?> TryAcquireFileMoveGateAsync(
        string sourceFile,
        string destinationFile,
        bool allowExistingAliasForRecovery = false)
    {
        if (!allowExistingAliasForRecovery
            && await IsFilesystemAliasAsync(sourceFile, destinationFile))
        {
            LogBlockedAlias(sourceFile, destinationFile);
            return null;
        }

        var sourceEndpoint = await ResolveFileMoveEndpointAsync(sourceFile);
        var destinationEndpoint = await ResolveFileMoveEndpointAsync(
            destinationFile);
        if (sourceEndpoint == null || destinationEndpoint == null)
        {
            _logger.LogWarning(
                "Blocked file move because endpoint identity could not be resolved: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return null;
        }
        if (AfterFileMoveEndpointsResolvedForTestAsync != null)
        {
            await AfterFileMoveEndpointsResolvedForTestAsync(
                sourceFile,
                destinationFile);
        }

        if (!allowExistingAliasForRecovery
            && await IsFilesystemAliasAsync(sourceFile, destinationFile))
        {
            LogBlockedAlias(sourceFile, destinationFile);
            return null;
        }

        var key = GetFileMoveGateKey(
            sourceEndpoint.LockIdentity,
            destinationEndpoint.LockIdentity);
        FileMoveGateEntry entry;
        lock (FileMoveGateRegistryLock)
        {
            if (!FileMoveGates.TryGetValue(key, out entry!))
            {
                entry = new FileMoveGateEntry();
                FileMoveGates.Add(key, entry);
            }

            entry.UserCount++;
        }

        await entry.Semaphore.WaitAsync();
        var stripeLocks = new List<FileStream>();
        PinnedDirectoryCreation.PinnedDirectoryAnchor? lockDirectory = null;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? sourceParent = null;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? destinationParent = null;
        FileMoveGateLease? lease = null;
        var leaseReturned = false;
        try
        {
            lockDirectory = OpenFileMoveLockDirectory();
            foreach (var lockName in GetFileMoveStripeLockNames(
                sourceEndpoint.LockIdentity,
                destinationEndpoint.LockIdentity))
            {
                stripeLocks.Add(
                    await lockDirectory.OpenOrCreateExclusiveLockFileAsync(
                        lockName));
            }

            var currentSource = await ResolveFileMoveEndpointAsync(sourceFile);
            var currentDestination = await ResolveFileMoveEndpointAsync(destinationFile);
            if (currentSource == null
                || currentDestination == null
                || !string.Equals(
                    currentSource.LockIdentity,
                    sourceEndpoint.LockIdentity,
                    StringComparison.Ordinal)
                || !string.Equals(
                    currentDestination.LockIdentity,
                    destinationEndpoint.LockIdentity,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "A file-move endpoint changed while its locks were acquired.");
            }

            var sourceParentPath = Path.GetDirectoryName(currentSource.ResolvedPath)
                ?? throw new IOException("The file-move source has no parent.");
            var destinationParentPath =
                Path.GetDirectoryName(currentDestination.ResolvedPath)
                ?? throw new IOException("The file-move destination has no parent.");
            sourceParent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                sourceParentPath,
                createMissing: false);
            destinationParent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                destinationParentPath,
                createMissing: false);
            var pinnedSource = await ResolveFileMoveEndpointAsync(sourceFile);
            var pinnedDestination = await ResolveFileMoveEndpointAsync(
                destinationFile);
            if (pinnedSource == null
                || pinnedDestination == null
                || !string.Equals(
                    pinnedSource.LockIdentity,
                    currentSource.LockIdentity,
                    StringComparison.Ordinal)
                || !string.Equals(
                    pinnedDestination.LockIdentity,
                    currentDestination.LockIdentity,
                    StringComparison.Ordinal)
                || !sourceParent.VisiblePathMatches()
                || !destinationParent.VisiblePathMatches())
            {
                throw new IOException(
                    "A file-move endpoint changed while its physical parents were pinned.");
            }

            lease = new FileMoveGateLease(
                key,
                entry,
                stripeLocks,
                lockDirectory,
                currentSource,
                currentDestination,
                sourceParent,
                destinationParent);
            if (!allowExistingAliasForRecovery
                && await IsFilesystemAliasAsync(sourceFile, destinationFile))
            {
                lease.Dispose();
                LogBlockedAlias(sourceFile, destinationFile);
                return null;
            }

            leaseReturned = true;
            return lease;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Blocked file move because cross-process path locks were unavailable: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return null;
        }
        finally
        {
            if (!leaseReturned)
            {
                if (lease != null)
                {
                    lease.Dispose();
                }
                else
                {
                    sourceParent?.Dispose();
                    destinationParent?.Dispose();
                    new FileMoveGateLease(
                        key,
                        entry,
                        stripeLocks,
                        lockDirectory,
                        sourceEndpoint,
                        destinationEndpoint,
                        sourceParent: null,
                        destinationParent: null).Dispose();
                }
            }
        }
    }

    private void LogBlockedAlias(string sourceFile, string destinationFile) =>
        _logger.LogWarning(
            "Blocked file move because source and destination are filesystem aliases: {Source} -> {Destination}",
            LogRedaction.SanitizeFilePath(sourceFile),
            LogRedaction.SanitizeFilePath(destinationFile));

    private async ValueTask<FileMoveEndpoint?> ResolveFileMoveEndpointAsync(
        string path)
    {
        var managedRoot = await ResolveManagedRootPathAsync(path);
        if (managedRoot.HasUnavailableOverlap)
        {
            return null;
        }

        FileSystemPathSemantics semantics;
        if (managedRoot.Semantics.HasValue)
        {
            semantics = managedRoot.Semantics.Value;
        }
        else
        {
            var resolver = _semanticsResolver ?? new FileSystemSemanticsResolver();
            var resolution = await resolver.ResolveAsync(
                path,
                FileSystemCaseSensitivityMode.Auto);
            if (resolution.State != PathIdentityState.Valid
                || resolution.Semantics.CaseSensitivity
                    == FileSystemCaseSensitivity.Unknown)
            {
                return null;
            }
            semantics = resolution.Semantics;
        }

        var fullPath = Path.GetFullPath(path);
        if (IsLinkedOrUnverifiableEntry(fullPath))
        {
            return null;
        }

        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            fullPath,
            semantics.Syntax);
        if (!TryResolvePhysicalPath(canonicalPath, out var physical))
        {
            return null;
        }

        var identity = physical.ResolvedPath;
        var lockIdentity = semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Insensitive
            ? identity.ToUpperInvariant()
            : identity;
        return new FileMoveEndpoint(lockIdentity, identity);
    }

    private async Task<bool> JournalPathsMatchGateAsync(
        FileMutationJournal journal,
        FileMoveGateLease gate) =>
        await PersistedPathMatchesEndpointAsync(
            journal.SourcePath,
            gate.SourceIdentity)
        && await PersistedPathMatchesEndpointAsync(
            journal.DestinationPath,
            gate.DestinationIdentity);

    private static bool JournalParentGenerationsMatchGate(
        FileMutationJournal journal,
        FileMoveGateLease gate)
    {
        if (string.IsNullOrWhiteSpace(
                journal.SourceParentDirectoryObjectIdentity)
            || string.IsNullOrWhiteSpace(
                journal.DestinationParentDirectoryObjectIdentity))
        {
            return false;
        }

        try
        {
            return gate.SourceParent.MatchesDirectoryObjectIdentity(
                    journal.SourceParentDirectoryObjectIdentity)
                && gate.DestinationParent.MatchesDirectoryObjectIdentity(
                    journal.DestinationParentDirectoryObjectIdentity);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PlatformNotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<bool> PersistedPathMatchesEndpointAsync(
        string persistedPath,
        string endpointIdentity)
    {
        var persistedEndpoint = await ResolveFileMoveEndpointAsync(persistedPath);
        return persistedEndpoint != null
            && string.Equals(
                persistedEndpoint.LockIdentity,
                endpointIdentity,
                StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> GetFileMoveStripeLockNames(
        string sourceIdentity,
        string destinationIdentity) =>
        new[] { sourceIdentity, destinationIdentity }
            .Select(GetFileMoveLockStripe)
            .Distinct()
            .Order()
            .Select(stripe => $"stripe-{stripe:D4}.lock")
            .ToArray();

    private static int GetFileMoveLockStripe(string path)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return (int)(BitConverter.ToUInt32(hash, 0) % FileMoveLockStripeCount);
    }

    private PinnedDirectoryCreation.PinnedDirectoryAnchor
        OpenFileMoveLockDirectory()
    {
        var directory = FileMoveLockDirectoryForTest;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _applicationPathService.FileMoveLockRootPath;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException(
                "An application-owned directory is required for file-move locks.");
        }

        var pinned = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            directory,
            createMissing: true);
        try
        {
            pinned.RestrictToCurrentUser();
            if (!pinned.VisiblePathMatches())
            {
                throw new IOException(
                    "The file-move lock directory changed while it was pinned.");
            }

            return pinned;
        }
        catch
        {
            pinned.Dispose();
            throw;
        }
    }

    private static string GetFileMoveGateKey(
        string sourceIdentity,
        string destinationIdentity)
    {
        var first = sourceIdentity;
        var second = destinationIdentity;
        if (string.CompareOrdinal(first, second) > 0)
        {
            (first, second) = (second, first);
        }

        return HashPathIdentity($"{first}\0{second}");
    }
}
