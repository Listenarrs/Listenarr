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
namespace Listenarr.Application.Audiobooks.RootFolders
{
    public partial class RootFolderService
    {
        internal Action? AfterInitialDirectoryIdentityCapturedForTest { get; set; }

        private async Task CaptureInitialDirectoryObjectIdentityAsync(RootFolder root)
        {
            var resolution = _directoryObjectIdentityResolver == null
                ? DirectoryObjectIdentityResolution.Unavailable(
                    "Directory object identity resolution is unavailable.")
                : await _directoryObjectIdentityResolver.ResolveAsync(root.Path);
            root.DirectoryObjectIdentityVersion = resolution.Version;
            root.DirectoryObjectIdentity = resolution.Value;
            root.DirectoryObjectIdentityUnavailableReason = resolution.UnavailableReason;
        }

        private async Task RevalidateCreatedDirectoryObjectIdentityAsync(RootFolder root)
        {
            if (_directoryObjectIdentityResolver == null
                || root.DirectoryObjectIdentityVersion == null
                || string.IsNullOrWhiteSpace(root.DirectoryObjectIdentity))
            {
                return;
            }

            var current = await _directoryObjectIdentityResolver.ResolveExistingAsync(
                root.Path,
                root.DirectoryObjectIdentityVersion.Value,
                root.DirectoryObjectIdentity,
                CancellationToken.None);
            if (current.IsAvailable
                && current.Version == root.DirectoryObjectIdentityVersion
                && string.Equals(
                    current.Value,
                    root.DirectoryObjectIdentity,
                    StringComparison.Ordinal))
            {
                return;
            }

            root.DirectoryObjectIdentityUnavailableReason =
                current.UnavailableReason
                    ?? "The root folder changed while its initial storage authorization was being committed.";
            root.UpdatedAt = DateTime.UtcNow;
            await _repo.UpdateAsync(root);
        }

    }
}
