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

namespace Listenarr.Tests.Features.Application.Search.Core
{
    public class SearchServiceFixesTests
    {
        [Fact]
        public void ParseMyAnonamouse_With_NoDateOrAge_Sets_Empty_PublishedDate()
        {
            var json = "[ { \"guid\": \"https://www.myanonamouse.net/t/100\", \"size\": 12345, \"title\": \"Test Title\" } ]";
            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var results = MyAnonamouseResponseParser.Parse(json, indexer, NullLogger.Instance);

            Assert.Single(results);
            var r = results[0];
            Assert.True(string.IsNullOrWhiteSpace(r.PublishedDate));
        }

        [Fact]
        public void ParseMyAnonamouse_Always_Sets_Grabs_Even_If_Zero()
        {
            var json = "[ { \"guid\": \"https://www.myanonamouse.net/t/101\", \"grabs\": \"0\", \"files\": \"1\", \"title\": \"Test Title 2\" } ]";
            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var results = MyAnonamouseResponseParser.Parse(json, indexer, NullLogger.Instance);

            Assert.Single(results);
            var r = results[0];
            Assert.Equal(0, r.Grabs);
            Assert.Equal(1, r.Files);
        }

        [Fact]
        public void ToSearchResult_DoesNot_Detect_Language_For_Usenet()
        {
            var idx = new IndexerSearchResult
            {
                Id = "u1",
                Title = "Some Title [ENG] Test",
                Artist = "Author",
                Size = 456,
                Seeders = 0,
                Leechers = 0,
                Quality = "",
                Grabs = 0,
                Files = 0,
                DownloadType = "Usenet",
                Source = "altHUB"
            };

            var sr = Listenarr.Domain.Search.SearchResultConverters.ToSearchResult(idx);
            Assert.Null(sr.Language);
        }

        [Fact]
        public void ToSearchResult_DoesNot_Preserve_Unknown_Language_From_Metadata()
        {
            var md = new MetadataSearchResult
            {
                Id = "m1",
                Title = "Metadata Title",
                Language = "Unknown",
                Source = "Audible",
                PublishYear = "2020"
            };

            var sr = Listenarr.Domain.Search.SearchResultConverters.ToSearchResult(md);
            Assert.Null(sr.Language);
        }

        [Fact]
        public void ToSearchResult_DoesNot_Preserve_Unknown_Quality_From_Indexer()
        {
            var idx = new IndexerSearchResult
            {
                Id = "i1",
                Title = "Quality Test",
                Size = 1000,
                Seeders = 10,
                Leechers = 2,
                Quality = "Unknown",
                Grabs = 0,
                Files = 0,
                DownloadType = "Torrent",
                Source = "test"
            };

            var sr = Listenarr.Domain.Search.SearchResultConverters.ToSearchResult(idx);
            Assert.Null(sr.Quality);
        }
    }
}
