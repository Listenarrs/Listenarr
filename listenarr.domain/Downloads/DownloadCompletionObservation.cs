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

using System.Globalization;

namespace Listenarr.Domain.Downloads
{
    /// <summary>
    /// When the download client first reported a download complete while the completion
    /// stability window holds its finalization back.
    /// </summary>
    /// <remarks>
    /// The hold writes the row back with its pre-completion status, so this is the only record
    /// that the client reported completion at all. It lives in the download's metadata so it
    /// survives a restart: the monitor reads it to resume the window, or to finalize a download
    /// the client stopped listing meanwhile, and orphan cleanup reads it to leave the row alone
    /// while that finalization is pending. It is set only while a hold is in progress and is
    /// cleared when the transition is let through or the client reports the download as no
    /// longer complete.
    /// </remarks>
    public static class DownloadCompletionObservation
    {
        public const string MetadataKey = "CompletionObservedAt";

        public static DateTime? GetCompletionObservedAt(this Download download) =>
            DateTime.TryParse(
                download.GetMetadataString(MetadataKey),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var observedAt)
                ? observedAt
                : null;

        public static void RecordCompletionObserved(this Download download, DateTime observedAtUtc) =>
            download.SetMetadata(MetadataKey, observedAtUtc.ToString("O", CultureInfo.InvariantCulture));

        public static void ClearCompletionObserved(this Download download) =>
            download.Metadata.Remove(MetadataKey);
    }
}
