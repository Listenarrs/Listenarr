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
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;
using Listenarr.Application.Interfaces;

namespace Listenarr.Tests.Features.Api.Services
{
    public class DownloadNaming_AudiobookMetadataTests : BaseTests
    {
        [Fact]
        public async Task ProcessCompletedDownload_UsesAudiobookMetadata_ForNaming()
        {
            // Create the output directory that the code will use as fallback
            var outputDir = FileService.GetTempDirectory("completed");

            // Create the expected subdirectory structure
            var authorDir = Path.Join(outputDir, "Jane Austen");
            var seriesDir = Path.Join(authorDir, "Pride and Prejudice");

            // Create a temporary source file
            var testFile = await FileService.GetTempFileAsync("dl-naming.mp3");

            // Create audiobook with Authors so naming should pick them
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Pride and Prejudice")
                .WithAuthor("Jane Austen")
                .WithBasePath(seriesDir)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId("dln-1")
                .WithAudiobook(book)
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(testFile)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .Build());

            // Act: call ProcessCompletedDownloadAsync which should generate a destination using audiobook metadata
            var downloadImportService = _provider.GetRequiredService<IDownloadImportService>();
            await downloadImportService.ImportDownloadFilesAsync(book, [testFile]);

            // Reload the download and audiobook files using a fresh DbContext instance
            var updated = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.True(updated.Status == DownloadStatus.Completed || updated.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {updated.Status}");

            var fileRecord = Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id));
            // Expect the author as a single folder name (with space preserved), not nested directories
            Assert.Contains("Jane Austen", fileRecord.Path);
        }
    }
}
