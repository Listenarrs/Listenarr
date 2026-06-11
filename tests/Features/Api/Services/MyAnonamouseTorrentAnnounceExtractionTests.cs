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

namespace Listenarr.Tests.Features.Api.Services
{
    public class MyAnonamouseTorrentAnnounceExtractionTests
    {
        [Fact]
        public void ExtractAnnounceUrls_FindsAnnounceAndAnnounceList()
        {
            // bencode: d8:announce26:http://tracker.example.com13:announce-listll12:http://a1.comel12:http://a2.comeee
            var sb = new StringBuilder();
            sb.Append("d");
            sb.Append("8:announce26:http://tracker.example.com");
            sb.Append("13:announce-listl");
            sb.Append("l13:http://a1.come");
            sb.Append("l13:http://a2.comee");
            sb.Append("e");

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());

            var urls = MyAnonamouseHelper.ExtractAnnounceUrls(bytes);

            Assert.Contains("http://tracker.example.com", urls);
            Assert.Contains("http://a1.com", urls);
            Assert.Contains("http://a2.com", urls);
        }

        [Fact]
        public void ExtractAnnounceUrls_FallsBackToUdpAndHttpRegex()
        {
            var ascii = "d4:spam4:eggse" + "19:http://hidden-tracker.example.com" + "10:someudpudp://1.2.3.4:6969/announce";
            var bytes = Encoding.ASCII.GetBytes(ascii);

            var urls = MyAnonamouseHelper.ExtractAnnounceUrls(bytes);

            Assert.Contains(urls, s => s.IndexOf("http://hidden-tracker.example.com", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(urls, s => s.IndexOf("udp://1.2.3.4:6969/announce", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
