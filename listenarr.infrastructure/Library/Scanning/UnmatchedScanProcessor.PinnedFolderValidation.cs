/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */

namespace Listenarr.Infrastructure.Library.Scanning
{
    public partial class UnmatchedScanProcessor
    {
        private static string? TryCapturePinnedFolderChangeToken(
            PinnedDirectoryCreation.PinnedDirectoryAnchor folder)
        {
            try
            {
                return folder.GetNamespaceChangeToken();
            }
            catch (Exception exception) when (
                exception is PlatformNotSupportedException
                    or NotSupportedException
                || exception is System.ComponentModel.Win32Exception native
                    && native.NativeErrorCode is 1 or 50 or 95)
            {
                return null;
            }
        }

        private static void EnsurePinnedFolderMatches(
            PinnedDirectoryCreation.PinnedDirectoryAnchor folder,
            string? expectedDirectoryIdentity,
            string? expectedNamespaceChangeToken)
        {
            if (!folder.VisiblePathMatches()
                || (!string.IsNullOrWhiteSpace(expectedDirectoryIdentity)
                    && !folder.MatchesDirectoryObjectIdentity(
                        expectedDirectoryIdentity))
                || (expectedNamespaceChangeToken != null
                    && !string.Equals(
                        folder.GetNamespaceChangeToken(),
                        expectedNamespaceChangeToken,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "The unmatched metadata folder changed after filesystem enumeration.");
            }
        }
    }
}
