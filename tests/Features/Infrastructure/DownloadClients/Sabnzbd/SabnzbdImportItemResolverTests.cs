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
using System.Collections.Specialized;
using System.Text.Json;
using System.Web;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.DownloadClients;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Sabnzbd
{
    public class SabnzbdImportItemResolverTests : BaseTests
    {
        private const string TargetNzoId = "SABnzbd_nzo_target";
        private const int PageSize = 100;
        private const int MaxPages = 10;

        private DownloadClientConfiguration _client = null!;
        private SabnzbdApiMock _sabnzbdApiMock = null!;
        private readonly string _storagePath = FileUtils.GetAbsolutePath("completed", "A Princess of Mars");

        public override async Task InitializeAsync()
        {
            _sabnzbdApiMock = _provider.GetRequiredService<SabnzbdApiMock>();
            _client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithHost("http://sabnzbd.example/sab")
                .WithPort(8080)
                .WithoutSsl()
                .WithApiKey("secret")
                .WithType("sabnzbd")
                .Build());
        }

        private static string Slot(string nzoId, string storage)
            => JsonSerializer.Serialize(new { nzo_id = nzoId, status = "Completed", storage });

        private static string HistoryJson(IEnumerable<string> slots)
            => "{\"history\": {\"slots\": [" + string.Join(",", slots) + "]}}";

        private static IEnumerable<string> OtherSlots(int count, int offset = 0)
            => Enumerable.Range(offset, count).Select(i => Slot($"SABnzbd_nzo_other_{i}", "/completed/other"));

        private async Task<string> ResolveContentPathAsync()
        {
            using var scope = _provider.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<SabnzbdImportItemResolver>();
            var resolved = await resolver.GetImportItemAsync(_client, new QueueItem { Id = TargetNzoId, ContentPath = "" });
            return resolved.ContentPath ?? "";
        }

        private List<NameValueCollection> HistoryQueries()
            => _sabnzbdApiMock.RequestHistory
                .Select(r => HttpUtility.ParseQueryString(r.RequestUri.Query))
                .Where(q => q["mode"] == "history")
                .ToList();

        [Fact]
        public async Task ResolveHistoryStorage_JobAbsentFromDefaultPage_FoundByNzoId()
        {
            _sabnzbdApiMock.HistoryQueryResponder = query => query["nzo_ids"] == TargetNzoId
                ? HistoryJson([Slot(TargetNzoId, _storagePath)])
                : HistoryJson(OtherSlots(10));

            var contentPath = await ResolveContentPathAsync();

            Assert.Equal(_storagePath, contentPath);
            Assert.Single(HistoryQueries());
        }

        [Fact]
        public async Task ResolveHistoryStorage_ServerIgnoresNzoIds_FoundOnSecondPage()
        {
            _sabnzbdApiMock.HistoryQueryResponder = query => query["start"] switch
            {
                "100" => HistoryJson(OtherSlots(5, PageSize).Prepend(Slot(TargetNzoId, _storagePath))),
                "0" => HistoryJson(OtherSlots(PageSize)),
                _ => HistoryJson(OtherSlots(10)),
            };

            var contentPath = await ResolveContentPathAsync();

            Assert.Equal(_storagePath, contentPath);
            Assert.Equal(["0", "100"], HistoryQueries().Where(q => q["start"] != null).Select(q => q["start"]));
        }

        [Fact]
        public async Task ResolveHistoryStorage_JobNotInHistory_StopsAtShortPage()
        {
            _sabnzbdApiMock.HistoryQueryResponder = query => query["nzo_ids"] != null
                ? HistoryJson([])
                : HistoryJson(OtherSlots(query["start"] == "0" ? PageSize : 3));

            var contentPath = await ResolveContentPathAsync();

            Assert.Equal("", contentPath);
            Assert.Equal(3, HistoryQueries().Count);
        }

        [Fact]
        public async Task ResolveHistoryStorage_JobNeverFound_ScanIsBounded()
        {
            _sabnzbdApiMock.HistoryQueryResponder = query => query["nzo_ids"] != null
                ? HistoryJson([])
                : HistoryJson(OtherSlots(PageSize));

            var contentPath = await ResolveContentPathAsync();

            Assert.Equal("", contentPath);
            Assert.Equal(1 + MaxPages, HistoryQueries().Count);
        }

        [Fact]
        public async Task ResolveHistoryStorage_FoundByNzoIdWithoutStorage_DoesNotScanPages()
        {
            _sabnzbdApiMock.HistoryQueryResponder = query => query["nzo_ids"] == TargetNzoId
                ? HistoryJson([$$"""{"nzo_id": "{{TargetNzoId}}", "status": "Failed", "storage": ""}"""])
                : HistoryJson(OtherSlots(10));

            var contentPath = await ResolveContentPathAsync();

            Assert.Equal("", contentPath);
            Assert.Single(HistoryQueries());
        }
    }
}
