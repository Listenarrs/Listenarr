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
using Listenarr.Application.Audiobooks.Playback;

namespace Listenarr.Tests.Audiobooks.Playback
{
    public class AudioContentTypeTests
    {
        [Theory]
        [InlineData("m4b", "audio/mp4")]
        [InlineData("M4A", "audio/mp4")]
        [InlineData("mp4", "audio/mp4")]
        [InlineData("aac", "audio/mp4")]
        [InlineData("mp3", "audio/mpeg")]
        [InlineData("ogg", "audio/ogg")]
        [InlineData("oga", "audio/ogg")]
        [InlineData("opus", "audio/ogg")]
        [InlineData("flac", "audio/flac")]
        [InlineData("wav", "audio/wav")]
        [InlineData(null, "application/octet-stream")]
        [InlineData("weirdext", "application/octet-stream")]
        public void ForContainer_ReturnsExpectedContentType(string? container, string expected)
        {
            Assert.Equal(expected, AudioContentType.ForContainer(container));
        }
    }
}
