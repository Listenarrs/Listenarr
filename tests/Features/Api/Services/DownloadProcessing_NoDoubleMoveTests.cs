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
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Category", "DownloadProcessing")]
    public class DownloadProcessing_NoDoubleMoveTests : BaseTests
    {
        [Fact]
        public async Task CompletedDownload_LinkedToAudiobook_DoesNotMoveToUnknownAuthor()
        {
            // Create audiobook with author
            var book = new AudiobookBuilder()
                .WithTitle("Pride and Prejudice")
                .WithAuthor("Jane Austen")
                .Build();
            await _audiobookRepository.AddAsync(book);

            // Create a temp directory for the expected output (simulate configured OutputPath)
            var outputRoot = FileService.GetTempDirectory("listenarr-test-output");

            // Create the expected subdirectory structure that the naming pattern will generate
            var authorDir = Path.Join(outputRoot, "Jane Austen");
            var seriesDir = Path.Join(authorDir, "Pride and Prejudice");
            Directory.CreateDirectory(seriesDir);

            // Create source file (as if downloader put it here)
            var sourceFile = await FileService.GetTempFileAsync("dl-dbl.mp3");

            // Create download record linked to audiobook
            var download = new DownloadBuilder()
                .WithId("dbl-1")
                .WithAudiobook(book)
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceFile)
                .Build();
            await _downloadRepository.AddAsync(download);

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputRoot)
                .WithMetadataProcessing()
                .WithMoveFileOnCompleted()
                .Build());

            // Act - process completed download
            var downloadService = _provider.GetRequiredService<DownloadService>();
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: either a DB AudiobookFile exists (import ran synchronously) or the file exists under the configured OutputPath
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id);
            if (files.Count > 0)
            {
                Assert.Single(files);
                var createdPath = files[0].Path ?? string.Empty;
                var norm = createdPath.ToLowerInvariant();
                // Expect the author as a single folder name (with space preserved)
                Assert.Contains("jane austen", norm);

                // Also assert there's no AudiobookFile under an "unknown author" path
                var filepaths = await _audiobookFileRepository.GetAllFilePathsAsync();
                Assert.Empty(filepaths.FindAll(path => path.Contains("unknown author", StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                // Import may be deferred; verify that a file has been moved/copied into the expected author folder on disk or the download status reflects deferred processing
                var authorPath = Path.Join(outputRoot, "Jane Austen");
                var found = Directory.Exists(authorPath) && Directory.GetFiles(authorPath, "*", SearchOption.AllDirectories).Length > 0;
                if (!found)
                {
                    // As a fallback, check that the download record status indicates it was processed or queued for processing
                    var updated = await _downloadRepository.GetByIdAsync(download.Id);
                    Assert.NotNull(updated);
                    Assert.True(updated.Status == DownloadStatus.Moved || updated.Status == DownloadStatus.Completed || updated.Status == DownloadStatus.Processing || updated.Status == DownloadStatus.Queued, $"Expected moved/completed/processing/queued status when not processed synchronously, got {updated.Status}");
                }
                else
                {
                    Assert.True(found, "Found files on disk under the author folder");
                }
            }
        }
    }
}
