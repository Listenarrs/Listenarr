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
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    [Trait("Name", "MyAnonamouseFreeleechTests")]
    [Trait("Category", "IndexerSearchProvider")]
    public class MyAnonamouseFreeleechTests
    {
        [Theory]
        [InlineData("Preferred")]
        [InlineData("Required")]
        public void ParseMyAnonamouse_Appends_Fl_When_FreeleechWedge_Enabled_And_Not_Already_Freeleech(string wedge)
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/123"",
    ""dl"": ""abc123"",
    ""title"": ""Test"",
    ""size"": 12345,
    ""free"": false,
    ""personal_freeleech"": false
  }
]";
            var indexer = new Indexer
            {
                Name = "MyAnonamouse",
                Url = "https://www.myanonamouse.net",
                Type = "Torrent",
                Implementation = "MyAnonamouse",
                AdditionalSettings = $"{{\"mam_id\":\"test_mam\",\"mam_options\":{{\"freeleechWedge\":\"{wedge}\"}}}}"
            };
            var results = MyAnonamouseResponseParser.Parse(json, indexer, NullLogger.Instance);

            var result = Assert.Single(results);
            Assert.Equal("https://www.myanonamouse.net/tor/download.php/abc123?mam_id=test_mam&fl=1", result.TorrentUrl);
        }

        [Fact]
        public void ParseMyAnonamouse_Does_Not_Append_Fl_When_Torrent_Is_Already_Freeleech()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/123"",
    ""dl"": ""abc123"",
    ""title"": ""Test"",
    ""size"": 12345,
    ""free"": true
  }
]";
            var indexer = new Indexer
            {
                Name = "MyAnonamouse",
                Url = "https://www.myanonamouse.net",
                Type = "Torrent",
                Implementation = "MyAnonamouse",
                AdditionalSettings = """{"mam_id":"test_mam","mam_options":{"freeleechWedge":"Preferred"}}"""
            };
            var results = MyAnonamouseResponseParser.Parse(json, indexer, NullLogger.Instance);

            var result = Assert.Single(results);
            Assert.Equal("https://www.myanonamouse.net/tor/download.php/abc123?mam_id=test_mam", result.TorrentUrl);
        }

        [Fact]
        public void ParseMyAnonamouse_Does_Not_Append_Fl_When_Personal_Freeleech()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/123"",
    ""dl"": ""abc123"",
    ""title"": ""Test"",
    ""size"": 12345,
    ""personal_freeleech"": true
  }
]";
            var indexer = new Indexer
            {
                Name = "MyAnonamouse",
                Url = "https://www.myanonamouse.net",
                Type = "Torrent",
                Implementation = "MyAnonamouse",
                AdditionalSettings = """{"mam_id":"test_mam","mam_options":{"freeleechWedge":"Required"}}"""
            };
            var results = MyAnonamouseResponseParser.Parse(json, indexer, NullLogger.Instance);

            var result = Assert.Single(results);
            Assert.Equal("https://www.myanonamouse.net/tor/download.php/abc123?mam_id=test_mam", result.TorrentUrl);
        }

        [Fact]
        public void ParseMyAnonamouse_Does_Not_Append_Fl_When_FreeleechWedge_Is_Never()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/123"",
    ""dl"": ""abc123"",
    ""title"": ""Test"",
    ""size"": 12345,
    ""free"": false
  }
]";
            var indexer = new Indexer
            {
                Name = "MyAnonamouse",
                Url = "https://www.myanonamouse.net",
                Type = "Torrent",
                Implementation = "MyAnonamouse",
                AdditionalSettings = """{"mam_id":"test_mam","mam_options":{"freeleechWedge":"Never"}}"""
            };
            var results = MyAnonamouseResponseParser.Parse(json, indexer, NullLogger.Instance);

            var result = Assert.Single(results);
            Assert.Equal("https://www.myanonamouse.net/tor/download.php/abc123?mam_id=test_mam", result.TorrentUrl);
        }

        [Fact]
        public void ParseMyAnonamouse_Appends_Fl_To_Tid_DownloadUrl_When_Wedge_Enabled()
        {
            var json = """
            [
              {
                "id": "1246262",
                "title": "A Parade of Horribles",
                "size": "1.1 GiB",
                "seeders": 2196,
                "leechers": 6,
                "free": false
              }
            ]
            """;
            var indexer = new Indexer
            {
                Name = "MyAnonamouse",
                Url = "https://www.myanonamouse.net",
                Type = "Torrent",
                Implementation = "MyAnonamouse",
                AdditionalSettings = """{"mam_id":"test_mam","mam_options":{"freeleechWedge":"Preferred"}}"""
            };

            var results = MyAnonamouseResponseParser.Parse(json, indexer, NullLogger.Instance);

            var result = Assert.Single(results);
            Assert.Equal(
                "https://www.myanonamouse.net/tor/download.php?tid=1246262&mam_id=test_mam&fl=1",
                result.TorrentUrl);
        }
    }
}
