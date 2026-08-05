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
using System.Runtime.ExceptionServices;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.RootFolders
{
    public partial class RootFolderService
    {
        public Task<RootFolder> ReauthorizeDirectoryIdentityAsync(
            int id,
            string expectedCurrentPath,
            CancellationToken cancellationToken = default) =>
            _mutationCoordinator.ExecuteExclusiveAsync(
                token => ReauthorizeDirectoryIdentityCoreAsync(
                    id,
                    expectedCurrentPath,
                    token),
                cancellationToken);

        private async Task<RootFolder> ReauthorizeDirectoryIdentityCoreAsync(
            int id,
            string expectedCurrentPath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentPath);
            var root = await _repo.GetByIdAsync(id)
                ?? throw new KeyNotFoundException("Root folder not found");
            await EnsureNoActiveRelocationAsync(root.Id);

            var persistedSemantics = RootFolderPathSemantics.ResolvePersisted(root)
                ?? throw new InvalidOperationException(
                    "Root filesystem semantics are unavailable; repair the persisted root path before reauthorizing its physical identity.");
            var normalizedExpectedPath = FileUtils.NormalizeRootFolderPathForStorage(
                expectedCurrentPath);
            if (!FileSystemPathIdentity.AreEquivalent(
                    root.Path,
                    normalizedExpectedPath,
                    persistedSemantics.Semantics))
            {
                throw new InvalidOperationException(
                    "Root folder path changed before physical identity reauthorization.");
            }

            var audiobookIds = await _repo.GetAllAudiobookIdsAsync();
            return await _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobookIds,
                async lockedToken =>
                {
                    await EnsureNoActiveMoveJobsTouchRootAsync(
                        root.Path,
                        persistedSemantics.Semantics);
                    if (_directoryObjectIdentityResolver == null)
                    {
                        throw new InvalidOperationException(
                            "Root folder physical identity cannot be reauthorized.");
                    }

                    var identity = await _directoryObjectIdentityResolver.ResolveAsync(
                        root.Path,
                        lockedToken);
                    if (!identity.IsAvailable)
                    {
                        throw new InvalidOperationException(
                            identity.UnavailableReason
                                ?? "The current root directory could not be enrolled safely.");
                    }

                    root.DirectoryObjectIdentityVersion = identity.Version;
                    root.DirectoryObjectIdentity = identity.Value;
                    root.DirectoryObjectIdentityUnavailableReason = null;
                    root.UpdatedAt = DateTime.UtcNow;
                    return await PersistRootWithEnrollmentCompensationAsync(
                        root,
                        identity,
                        () => _repo.UpdateAsync(root));
                },
                cancellationToken);
        }

        private async Task<DirectoryObjectIdentityResolution>
            CaptureInitialDirectoryObjectIdentityAsync(RootFolder root)
        {
            var resolution = _directoryObjectIdentityResolver == null
                ? DirectoryObjectIdentityResolution.Unavailable(
                    "Directory object identity resolution is unavailable.")
                : await _directoryObjectIdentityResolver.ResolveAsync(root.Path);
            root.DirectoryObjectIdentityVersion = resolution.Version;
            root.DirectoryObjectIdentity = resolution.Value;
            root.DirectoryObjectIdentityUnavailableReason = resolution.UnavailableReason;
            return resolution;
        }

        private async Task<RootFolder> PersistRootWithEnrollmentCompensationAsync(
            RootFolder root,
            DirectoryObjectIdentityResolution identity,
            Func<Task> persistAsync)
        {
            try
            {
                await persistAsync();
                return root;
            }
            catch (Exception persistenceException)
            {
                RootFolder? durableRoot;
                try
                {
                    durableRoot = root.Id > 0
                        ? await _repo.GetByIdAsync(root.Id)
                        : await _repo.GetByPathAsync(root.Path);
                }
                catch (Exception verificationException)
                {
                    throw new InvalidOperationException(
                        "Root folder persistence failed and its durable outcome could not be verified; the physical enrollment marker was preserved.",
                        new AggregateException(
                            persistenceException,
                            verificationException));
                }

                if (durableRoot != null
                    && durableRoot.DirectoryObjectIdentityVersion == identity.Version
                    && string.Equals(
                        durableRoot.DirectoryObjectIdentity,
                        identity.Value,
                        StringComparison.Ordinal))
                {
                    return durableRoot;
                }

                if (identity.EnrollmentCreated
                    && identity.Version.HasValue
                    && !string.IsNullOrWhiteSpace(identity.Value)
                    && _directoryObjectIdentityResolver != null)
                {
                    try
                    {
                        await _directoryObjectIdentityResolver.RetireEnrollmentAsync(
                            root.Path,
                            identity.Version.Value,
                            identity.Value,
                            CancellationToken.None);
                    }
                    catch (Exception compensationException)
                    {
                        throw new InvalidOperationException(
                            "Root folder persistence failed and its newly created physical enrollment could not be retired safely.",
                            new AggregateException(
                                persistenceException,
                                compensationException));
                    }
                }

                ExceptionDispatchInfo.Capture(persistenceException).Throw();
                throw new InvalidOperationException("Unreachable persistence compensation state.");
            }
        }

        private async Task ValidateExistingDirectoryObjectIdentityAsync(RootFolder root)
        {
            if (root.DirectoryObjectIdentityVersion == null
                || string.IsNullOrWhiteSpace(root.DirectoryObjectIdentity))
            {
                return;
            }
            if (_directoryObjectIdentityResolver == null)
            {
                throw new InvalidOperationException(
                    "Root folder physical identity cannot be validated.");
            }

            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRootPath,
                    out var pathReason))
            {
                throw new InvalidOperationException(pathReason);
            }

            var current = await _directoryObjectIdentityResolver.ResolveExistingAsync(
                canonicalRootPath);
            if (!current.IsAvailable
                || current.Version != root.DirectoryObjectIdentityVersion
                || !string.Equals(
                    current.Value,
                    root.DirectoryObjectIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The configured root folder now identifies a different physical directory; use an explicit path-change operation to reauthorize it.");
            }
        }
    }
}
