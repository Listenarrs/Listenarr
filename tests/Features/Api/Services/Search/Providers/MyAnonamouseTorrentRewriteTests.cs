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
using Xunit;
using Listenarr.Api.Services;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    public class MyAnonamouseTorrentRewriteTests
    {
        [Fact]
        public void ReplaceHostInTorrent_ReplacesIpWithHost()
        {
            // Construct minimal bencoded torrent with announce containing IP
            var announce = "http://47.39.239.96/announce";
            var bencoded = $"d8:announce{announce.Length}:{announce}4:infod6:lengthi123e4:name6:testee";
            var bytes = Encoding.ASCII.GetBytes(bencoded);

            var replaced = MyAnonamouseHelper.ReplaceHostInTorrent(bytes, "47.39.239.96", "www.myanonamouse.net");
            var s = Encoding.ASCII.GetString(replaced);
            Assert.Contains("www.myanonamouse.net", s);
            Assert.DoesNotContain("47.39.239.96", s);
        }
    }
}
