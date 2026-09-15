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
using Microsoft.AspNetCore.Http;
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

        private static readonly TrustedDownloadCandidate Candidate = new(
            Id: "release-1",
            Title: "A Book",
            Artist: "An Author",
            Album: "A Book",
            Source: "stub-indexer",
            Quality: null,
            Language: null,
            Size: 1024,
            Seeders: 4,
            SourceDescriptor: new DownloadSourceDescriptor(
                IndexerId: 1,
                IndexerImplementation: "Torznab",
                Protocol: DownloadProtocol.Torrent,
                Locators: [new DownloadSourceLocator(
                    DownloadSourceLocatorKind.Magnet,
                    "magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12")]));

        private static DownloadController SendController(Exception thrown)
        {
            var downloadService = new Mock<IDownloadService>(MockBehavior.Loose);
            downloadService
                .Setup(service => service.SendToDownloadClientAsync(
                    It.IsAny<TrustedDownloadCandidate>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>()))
                .ThrowsAsync(thrown);

            var references = new Mock<IDownloadReferenceService>(MockBehavior.Loose);
            references
                .Setup(service => service.Read(It.IsAny<string>()))
                .Returns(Candidate);

            return new DownloadController(
                downloadService.Object,
                Mock.Of<IDownloadQueueService>(),
                Mock.Of<IDownloadProcessingJobService>(),
                NullLogger<DownloadController>.Instance,
                references.Object);
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

        // The same exception reaches the manual send endpoint fifty lines below the hunk, where
        // the pre-existing catch on the base type answered 502 Bad Gateway. Nothing upstream of
        // the client is broken, so the gateway code was telling the caller the wrong thing about
        // the more deliberate of the two actions. 409 is what this endpoint already answers when
        // a download for the audiobook is active, which is the same situation from the database
        // side.
        [Fact]
        [Trait("Scenario", "A manual send of a release the client already holds is a conflict")]
        public async Task SendToDownloadClient_WhenTheClientRefusesTheRelease_Answers409()
        {
            var controller = SendController(new DownloadClientRejectedReleaseException(
                "qBittorrent already holds this release, so it refused the torrent with HTTP 409."));

            var response = await controller.SendToDownloadClient(new SendDownloadRequest
            {
                DownloadReference = "reference"
            });

            var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
            Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
            Assert.Contains(
                "already holds this release",
                conflict.Value?.ToString() ?? string.Empty,
                StringComparison.Ordinal);
        }

        // The control. The rejected-release type derives from the submission type, so the two
        // catches are ordered rather than disjoint. Put the narrow one after the broad one, or
        // widen it to the base type, and a download client that is genuinely unreachable starts
        // reporting as "you already have this" and the operator is sent looking in the wrong
        // place.
        [Fact]
        [Trait("Scenario", "A genuine submission failure is still a bad gateway")]
        public async Task SendToDownloadClient_WhenTheClientIsBroken_StillAnswers502()
        {
            var controller = SendController(new DownloadClientSubmissionException(
                "qBittorrent rejected the torrent with HTTP 500."));

            var response = await controller.SendToDownloadClient(new SendDownloadRequest
            {
                DownloadReference = "reference"
            });

            var status = Assert.IsType<ObjectResult>(response.Result);
            Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        }
    }
}
