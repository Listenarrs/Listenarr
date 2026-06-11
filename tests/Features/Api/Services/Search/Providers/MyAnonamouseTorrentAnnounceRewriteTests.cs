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
using System.Text;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    public class MyAnonamouseTorrentAnnounceRewriteTests
    {
        [Fact]
        public void ReplaceHostInTorrent_ReplacesTrackerHostWithIndexHost()
        {
            // announce contains tracker host with passkey in path
            var announce = "https://t.myanonamouse.net/tracker.php/mGDjyetAEBGCaneLZNS9OHawTo1upcwU/announce";
            var bencoded = $"d8:announce{announce.Length}:{announce}4:infod6:lengthi123e4:name6:testee";
            var bytes = Encoding.ASCII.GetBytes(bencoded);

            var replaced = MyAnonamouseHelper.ReplaceHostInTorrent(bytes, "t.myanonamouse.net", "www.myanonamouse.net");
            var s = Encoding.ASCII.GetString(replaced);
            Assert.Contains("www.myanonamouse.net", s);
            Assert.Contains("/tracker.php/mGDjyetAEBGCaneLZNS9OHawTo1upcwU/announce", s); // passkey/path preserved
            Assert.DoesNotContain("t.myanonamouse.net", s);
        }
    }
}
