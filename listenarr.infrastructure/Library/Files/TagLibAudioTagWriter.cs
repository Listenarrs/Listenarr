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

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Files
{
    public class TagLibAudioTagWriter : IAudioTagWriter
    {
        private readonly ILogger<TagLibAudioTagWriter> _logger;

        public TagLibAudioTagWriter(ILogger<TagLibAudioTagWriter> logger)
        {
            _logger = logger;
        }

        public Task WriteAsinTagAsync(string filePath, string asin)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(asin))
                return Task.CompletedTask;

            try
            {
                using var file = TagLib.File.Create(filePath);
                ApplyAsinTag(file, asin);
                file.Save();
                _logger.LogDebug("Wrote ASIN tag '{Asin}' to {File}", asin, LogRedaction.SanitizeFilePath(filePath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to write ASIN tag to {File} - import will continue", LogRedaction.SanitizeFilePath(filePath));
            }

            return Task.CompletedTask;
        }

        public Task WriteAsinTagAsync(
            IAudiobookFileRegistrationLease registrationLease,
            string asin)
        {
            ArgumentNullException.ThrowIfNull(registrationLease);
            if (string.IsNullOrWhiteSpace(asin))
            {
                return Task.CompletedTask;
            }
            if (!registrationLease.HasDurablePhysicalObjectIdentity)
            {
                _logger.LogDebug(
                    "Skipped ASIN tag write for scan-only file {File} because the registration lease has no durable physical identity.",
                    LogRedaction.SanitizeFilePath(registrationLease.PublicPath));
                return Task.CompletedTask;
            }

            try
            {
                using var file = TagLib.File.Create(
                    new RegistrationLeaseFileAbstraction(registrationLease));
                ApplyAsinTag(file, asin);
                file.Save();
                _logger.LogDebug(
                    "Wrote ASIN tag '{Asin}' to generation-bound file {File}",
                    asin,
                    LogRedaction.SanitizeFilePath(registrationLease.PublicPath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write ASIN tag to generation-bound file {File} - import will continue",
                    LogRedaction.SanitizeFilePath(registrationLease.PublicPath));
            }

            return Task.CompletedTask;
        }

        private static void ApplyAsinTag(TagLib.File file, string asin)
        {
            // An MPEG-4 file's Tag is a CombinedTag wrapping the Apple tag, never the AppleTag
            // itself, so a type test on file.Tag matches nothing here and the save that follows
            // writes an unchanged file while still reporting success. The tag has to be asked
            // for by type. Only MPEG-4 answers to Apple, so mp3 and flac fall through as before.
            if (file.GetTag(TagLib.TagTypes.Apple, create: true) is TagLib.Mpeg4.AppleTag appleTag)
                appleTag.SetDashBox("com.apple.iTunes", "ASIN", asin);
            else if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3Tag)
            {
                var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, "ASIN", true);
                frame.Text = new[] { asin };
            }
            else if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                xiph.SetField("ASIN", asin);
        }

        private sealed class RegistrationLeaseFileAbstraction(
            IAudiobookFileRegistrationLease registrationLease)
            : TagLib.File.IFileAbstraction
        {
            public string Name => registrationLease.PublicPath;

            public Stream ReadStream => registrationLease.OpenMetadataReadStream();

            public Stream WriteStream => registrationLease.OpenMetadataWriteStream();

            public void CloseStream(Stream stream) => stream.Dispose();
        }
    }
}
