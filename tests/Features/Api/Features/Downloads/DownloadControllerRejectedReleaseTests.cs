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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads
{
    /// <summary>
    /// The half of the 409 change that lives in the API. Nothing else asserts it: the adapter
    /// tests stop at the exception type, so the catch that turns a refusal into an answer the
    /// user can read could be deleted with the rest of the suite still green.
    /// </summary>
    [Trait("Area", "DownloadsApi")]
    [Trait("Name", "DownloadControllerRejectedReleaseTests")]
    [Trait("Category", "DownloadsController")]
    public sealed class DownloadControllerRejectedReleaseTests : BaseTests
    {
        private static DownloadController Controller(Exception thrown)
        {
            var downloadService = new Mock<IDownloadService>(MockBehavior.Loose);
            downloadService
                .Setup(service => service.SearchAndDownloadAsync(It.IsAny<int>()))
                .ThrowsAsync(thrown);

            return new DownloadController(
                downloadService.Object,
                Mock.Of<IDownloadQueueService>(),
                Mock.Of<IDownloadProcessingJobService>(),
                NullLogger<DownloadController>.Instance);
        }

        [Fact]
        [Trait("Scenario", "A release the client already holds is an answer, not a server error")]
        public async Task SearchAndDownload_WhenTheClientRefusesTheRelease_AnswersOkWithTheReason()
        {
            var controller = Controller(new DownloadClientRejectedReleaseException(
                "qBittorrent already holds this release, so it refused the torrent with HTTP 409."));

            var response = await controller.SearchAndDownload(new SearchAndDownloadRequest { AudiobookId = 7 });

            var ok = Assert.IsType<OkObjectResult>(response.Result);
            var payload = Assert.IsType<SearchAndDownloadResult>(ok.Value);
            Assert.False(payload.Success);
            Assert.Equal(
                "qBittorrent already holds this release, so it refused the torrent with HTTP 409.",
                payload.Message);
        }

        [Fact]
        [Trait("Scenario", "A genuine submission failure is still a server error")]
        public async Task SearchAndDownload_WhenTheClientIsBroken_StillAnswers500()
        {
            // The control. Widening the catch to the base type would hide a broken client behind
            // a 200 that says the search simply found nothing worth taking.
            var controller = Controller(new DownloadClientSubmissionException(
                "qBittorrent rejected the torrent with HTTP 500."));

            var response = await controller.SearchAndDownload(new SearchAndDownloadRequest { AudiobookId = 7 });

            var status = Assert.IsType<ObjectResult>(response.Result);
            Assert.Equal(500, status.StatusCode);
        }
    }
}
