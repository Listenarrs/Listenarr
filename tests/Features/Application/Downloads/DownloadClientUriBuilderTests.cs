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
using Listenarr.Application.Downloads;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    public class DownloadClientUriBuilderTests
    {
        [Fact]
        public void ResolveTorrentAddTarget_PrefersValidatedMagnetLink()
        {
            var result = new SearchResult
            {
                MagnetLink = " magnet:?xt=urn:btih:abc123 ",
                TorrentUrl = "https://example.com/file.torrent"
            };

            var target = DownloadClientUriBuilder.ResolveTorrentAddTarget(result);

            Assert.True(target.IsMagnet);
            Assert.Equal("magnet:?xt=urn:btih:abc123", target.Value);
        }

        [Fact]
        public void ResolveTorrentAddTarget_RejectsInvalidMagnetScheme()
        {
            var result = new SearchResult
            {
                MagnetLink = "https://example.com/not-a-magnet"
            };

            var ex = Assert.Throws<ArgumentException>(() => DownloadClientUriBuilder.ResolveTorrentAddTarget(result));

            Assert.Contains("magnet scheme", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ResolveTorrentAddTarget_RejectsNonHttpTorrentUrl()
        {
            var result = new SearchResult
            {
                TorrentUrl = "ftp://example.com/file.torrent"
            };

            var ex = Assert.Throws<ArgumentException>(() => DownloadClientUriBuilder.ResolveTorrentAddTarget(result));

            Assert.Contains("HTTP or HTTPS", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
