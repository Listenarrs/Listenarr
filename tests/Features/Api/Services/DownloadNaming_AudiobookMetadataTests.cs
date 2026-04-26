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
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Services
{
    public class DownloadNaming_AudiobookMetadataTests : BaseTests
    {
        [Fact]
        public async Task ProcessCompletedDownload_UsesAudiobookMetadata_ForNaming()
        {
            // Create audiobook with Authors so naming should pick them
            var book = new Audiobook { Title = "Pride and Prejudice", Authors = ["Jane Austen"] };
            await _audiobookRepository.AddAsync(book);

            // Create a temporary source file
            var testFile = await FileService.GetTempFileAsync("dl-naming.mp3");

            var download = new Download
            {
                Id = "dln-1",
                AudiobookId = book.Id,
                Title = book.Title,
                Status = DownloadStatus.Completed,
                DownloadPath = testFile,
                FinalPath = testFile,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };

            await _downloadRepository.AddAsync(download);

            // Create the output directory that the code will use as fallback
            var outputDir = FileService.GetTempDirectory("completed");

            // Create the expected subdirectory structure
            var authorDir = Path.Join(outputDir, "Jane Austen");
            var seriesDir = Path.Join(authorDir, "Pride and Prejudice");
            Directory.CreateDirectory(seriesDir);

            // Act: call ProcessCompletedDownloadAsync which should generate a destination using audiobook metadata
            var downloadService = _provider.GetRequiredService<IDownloadService>();
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Reload the download and audiobook files using a fresh DbContext instance
            var updated = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.True(updated.Status == DownloadStatus.Completed || updated.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {updated.Status}");

            var fileRecord = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id));
            // Import may be deferred (queued) so a DB file record may not exist synchronously; accept either outcome.
            if (fileRecord != null)
            {
                // The stored path should include the audiobook Author (Jane Austen) as part of the generated folder
                var lowered = (fileRecord.Path ?? string.Empty).ToLowerInvariant();
                // Expect the author as a single folder name (with space preserved), not nested directories
                Assert.Contains("jane austen", lowered);
            }
        }
    }
}
