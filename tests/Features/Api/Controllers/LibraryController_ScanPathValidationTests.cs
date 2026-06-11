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
using Microsoft.AspNetCore.Mvc;
using Listenarr.Api.Controllers;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Controllers
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_ScanPathValidationTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_ScanPathValidationTests : BaseTests
    {
        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "AllowsRequestPathWithinConfiguredRoot_ReturnsOk")]
        public async Task ScanAudiobook_AllowsRequestPathWithinConfiguredRoot_ReturnsOk()
        {
            // Given
            var tempRoot = FileService.GetTempDirectory("listenarr-test-root");

            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();

            await _rootFolderRepository.AddAsync(new RootFolder
            {
                Name = "root",
                Path = tempRoot
            });

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(FileService.GetTempPath())
                .Build());

            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .Build());

            var request = new LibraryController.ScanRequest { Path = tempRoot };

            // When
            var result = await controller.ScanAudiobookFiles(ab.Id, request);

            // Then
            Assert.IsType<OkObjectResult>(result);
            var ok = (OkObjectResult)result;
            Assert.Equal(200, ok.StatusCode);
            Assert.Contains("No files found", ok.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        [Trait("Method", "ScanAudiobookFiles")]
        [Trait("Scenario", "RejectsRequestPathOutsideConfiguredRoots_ReturnsBadRequest")]
        public async Task ScanAudiobook_RejectsRequestPathOutsideConfiguredRoots_ReturnsBadRequest()
        {
            // Given
            var tempRoot = FileService.GetTempDirectory("listenarr-test-root");
            var other = FileService.GetTempDirectory("listenarr-other");

            Init(services => services.Without<IScanQueueService>());
            var controller = _provider.GetRequiredService<LibraryController>();

            await _rootFolderRepository.AddAsync(new RootFolder
            {
                Name = "root",
                Path = tempRoot
            });

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(Path.Join(FileService.GetTempPath(), "different-root"))
                .Build());

            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Test")
                .Build());

            var request = new LibraryController.ScanRequest { Path = other };

            // When
            var result = await controller.ScanAudiobookFiles(ab.Id, request);

            // Then
            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, bad.StatusCode);
            Assert.Contains("not within configured root folders", bad.Value?.ToString() ?? string.Empty);
        }
    }
}
