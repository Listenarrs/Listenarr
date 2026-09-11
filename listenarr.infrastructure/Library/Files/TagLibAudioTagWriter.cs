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
            => WriteTagsAsync(registrationLease, asin, coverArt: null);

        public Task WriteTagsAsync(
            IAudiobookFileRegistrationLease registrationLease,
            string? asin,
            AudioCoverArt? coverArt)
        {
            ArgumentNullException.ThrowIfNull(registrationLease);
            if (string.IsNullOrWhiteSpace(asin) && coverArt is null)
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

                if (!string.IsNullOrWhiteSpace(asin))
                {
                    ApplyAsinTag(file, asin);
                }

                var wroteCover = coverArt is not null && ApplyCoverArt(file, coverArt);
                file.Save();
                _logger.LogDebug(
                    "Wrote tags to generation-bound file {File}. ASIN: {Asin}, cover art embedded: {Cover}",
                    LogRedaction.SanitizeFilePath(registrationLease.PublicPath),
                    asin ?? "(none)",
                    wroteCover);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write tags to generation-bound file {File} - import will continue",
                    LogRedaction.SanitizeFilePath(registrationLease.PublicPath));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Replace the file's embedded pictures with the supplied artwork.
        ///
        /// Replacing rather than appending: a file that already carries a cover and gains a
        /// second one shows whichever the player happens to pick first, which looks like a
        /// bug to whoever asked for the artwork to be corrected. Returns false when the
        /// container reports no tag to write into, so the caller can say so rather than
        /// claim a write that did not happen.
        /// </summary>
        internal static bool ApplyCoverArt(TagLib.File file, AudioCoverArt coverArt)
        {
            if (file.Tag is null)
            {
                return false;
            }

            var picture = new TagLib.Picture(new TagLib.ByteVector(coverArt.Data))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = coverArt.MimeType,
                Description = "Cover"
            };

            file.Tag.Pictures = new TagLib.IPicture[] { picture };
            return true;
        }

        private static void ApplyAsinTag(TagLib.File file, string asin)
        {
            if (file.Tag is TagLib.Mpeg4.AppleTag appleTag)
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
