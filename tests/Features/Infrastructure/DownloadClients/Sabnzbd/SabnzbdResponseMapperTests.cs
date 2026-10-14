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
namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Sabnzbd
{
    [Trait("Name", "SabnzbdResponseMapperTests")]
    [Trait("Category", "SabnzbdResponseMapper")]
    public sealed class SabnzbdResponseMapperTests
    {
        /// <summary>
        /// Regression test for https://github.com/Listenarrs/Listenarr/issues/839
        /// SABnzbd reports an item as "Completed" in the active queue before it is
        /// archived to history with a real storage path. Mapping that slot to a
        /// completed QueueItem let downloads reach DownloadStatus.Completed with an
        /// empty DownloadPath, permanently blocking import.
        /// </summary>
        [Fact]
        public void MapQueueSlotToQueueItem_CompletedStatusWithoutStorage_ReturnsNull()
        {
            // Given: an active-queue slot reporting Completed with no storage field yet
            var client = new DownloadClientConfiguration { DownloadPath = "/downloads" };
            using var document = System.Text.Json.JsonDocument.Parse(
                """
                {
                  "nzo_id": "sab-race-1",
                  "filename": "Book",
                  "status": "Completed",
                  "percentage": "100",
                  "mb": "100",
                  "mbleft": "0"
                }
                """);

            // When
            var item = SabnzbdResponseMapper.MapQueueSlotToQueueItem(
                client,
                document.RootElement,
                configuredCategory: string.Empty,
                speed: 0);

            // Then: excluded rather than reported as a pathless completion
            Assert.Null(item);
        }

        [Fact]
        public void MapQueueSlotToQueueItem_CompletedStatusWithStorage_ReturnsCompletedItem()
        {
            // Given: an active-queue slot reporting Completed with a real storage path
            var client = new DownloadClientConfiguration { DownloadPath = "/downloads" };
            using var document = System.Text.Json.JsonDocument.Parse(
                """
                {
                  "nzo_id": "sab-race-2",
                  "filename": "Book",
                  "status": "Completed",
                  "percentage": "100",
                  "mb": "100",
                  "mbleft": "0",
                  "storage": "/downloads/complete/Book"
                }
                """);

            // When
            var item = SabnzbdResponseMapper.MapQueueSlotToQueueItem(
                client,
                document.RootElement,
                configuredCategory: string.Empty,
                speed: 0);

            // Then: a real storage path still resolves to a completed item, unaffected
            Assert.NotNull(item);
            Assert.Equal("completed", item!.Status);
            Assert.Equal("/downloads/complete/Book", item.ContentPath);
        }

        [Fact]
        public void MapQueueSlotToQueueItem_DownloadingStatusWithoutStorage_StillReturnsItem()
        {
            // Given: a genuinely still-downloading slot, which never carries storage
            var client = new DownloadClientConfiguration { DownloadPath = "/downloads" };
            using var document = System.Text.Json.JsonDocument.Parse(
                """
                {
                  "nzo_id": "sab-race-3",
                  "filename": "Book",
                  "status": "Downloading",
                  "percentage": "50",
                  "mb": "100",
                  "mbleft": "50"
                }
                """);

            // When
            var item = SabnzbdResponseMapper.MapQueueSlotToQueueItem(
                client,
                document.RootElement,
                configuredCategory: string.Empty,
                speed: 0);

            // Then: the new guard only targets "completed" - in-progress items are unaffected
            Assert.NotNull(item);
            Assert.Equal("downloading", item!.Status);
        }
    }
}
