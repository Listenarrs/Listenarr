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
using Listenarr.Tests.Common;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Infrastructure.FileSystem;

namespace Listenarr.Tests.Features.Api.Services
{
    public class MoveBackgroundService_FilePathPreservationTests : BaseTests
    {
        [Fact(Timeout = 20000)]
        public async Task MoveBackgroundService_UpdatesLegacyFilePath_WhenFileExistsInTarget()
        {
            // Register a simple metadata service mock
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>())).ReturnsAsync(new AudioMetadata { Duration = TimeSpan.FromSeconds(1), Format = "m4b" });
            _services.AddSingleton(metadataMock.Object);
            Init();

            // Create source and target dirs
            var source = FileService.GetTempDirectory("listenarr_test_move_src");
            var destination = FileService.GetTempDirectory("listenarr_test_move_dst");

            var audioFileName = "dune.m4b";
            var unprocessedFile = await FileService.GetFileAsync(source, audioFileName);

            var ab = new Audiobook { Title = "MoveFilePathTest", BasePath = source, FilePath = unprocessedFile };
            await _audiobookRepository.AddAsync(ab);

            var moveQueue = _provider.GetRequiredService<IMoveQueueService>();
            var bg = _provider.GetRequiredService<MoveBackgroundService>();

            // Start background service
            await bg.StartAsync(CancellationToken.None);

            // Enqueue move (include source so move uses our exact directory)
            var jobId = await moveQueue.EnqueueMoveAsync(ab.Id, destination, source);

            // Poll for completion
            var succeeded = false;
            for (int i = 0; i < 60; i++)
            {
                if (moveQueue.TryGetJob(jobId, out var job) && string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    succeeded = true; break;
                }
                await Task.Delay(250, CancellationToken.None);
            }

            await bg.StopAsync(CancellationToken.None);

            Assert.True(succeeded, "Move job did not complete in time");

            // Refresh audiobook
            using var scope = _provider.CreateScope();
            _audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var audiobook = await _audiobookRepository.GetByIdAsync(ab.Id);
            Assert.NotNull(audiobook);

            var processedFile = Path.GetFullPath(Path.Join(destination, audioFileName));

            // The file should have been moved to target
            Assert.True(File.Exists(processedFile), "Moved file not found at expected target path");
            // Original should not exist
            Assert.False(File.Exists(unprocessedFile), "Source file should have been deleted after move");

            // The audiobook's legacy FilePath should have been updated to the new path
            Assert.Equal(processedFile, audiobook.FilePath);

            // Now verify AudioFileService will accept/associate the moved file
            var audiobookFileService = _provider.GetRequiredService<IAudiobookFileService>();

            var created = await audiobookFileService.EnsureAudiobookFileAsync(audiobook, processedFile, "test");
            Assert.True(created, "AudioFileService failed to associate moved file even though FilePath was updated");

            var fileRecord = (await _audiobookFileRepository.GetByAudiobookIdAsync(ab.Id)).First(f => f.Path == processedFile);
            Assert.NotNull(fileRecord);
        }
    }
}
