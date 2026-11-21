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
using System.Text.Json;
using Listenarr.Infrastructure.Ffmpeg.Metadata;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Metadata
{
    // Content-probe stream detection behind Listenarr#890. A bare .mp4 is an ambiguous
    // container, so FileUtils.IsProbedAudioContent decides admission from what the probe found
    // rather than from the extension. These tests pin the mapping from ffprobe's -show_streams
    // JSON onto AudioMetadata.HasAudioStream / HasVideoStream, and the verdict FileUtils draws
    // from the pair. The critical, easy-to-get-wrong case is embedded cover art: ffprobe reports
    // it as a video stream, and an audiobook whose only non-audio stream is its cover must still
    // be admitted.
    [Trait("Name", "FfprobeStreamPresenceTests")]
    [Trait("Category", "FfprobeMetadataMapper")]
    public class FfprobeStreamPresenceTests : BaseTests
    {
        private static AudioMetadata Map(string ffprobeJson)
        {
            using var document = JsonDocument.Parse(ffprobeJson);
            return FfprobeMetadataMapper.Map(document.RootElement.Clone(), "Target Book.mp4");
        }

        [Fact]
        [Trait("Method", "Map")]
        public void AudioOnlyContainer_IsAdmittedAsAudio()
        {
            const string json = """
                {
                  "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "3600.0" },
                  "streams": [
                    { "codec_type": "audio", "codec_name": "aac", "channels": 2, "sample_rate": "44100" }
                  ]
                }
                """;

            var metadata = Map(json);

            Assert.True(metadata.HasAudioStream);
            Assert.False(metadata.HasVideoStream);
            Assert.True(FileUtils.IsProbedAudioContent(metadata));
        }

        // The control that must come out differently from the audio-only case above: the only
        // thing added is a genuine video stream (no attached_pic disposition), and that alone
        // has to flip the verdict to rejected. Same audio stream, opposite admission.
        [Fact]
        [Trait("Method", "Map")]
        public void ContainerWithPlayableVideo_IsRejected()
        {
            const string json = """
                {
                  "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "3600.0" },
                  "streams": [
                    { "codec_type": "audio", "codec_name": "aac", "channels": 2, "sample_rate": "44100" },
                    { "codec_type": "video", "codec_name": "h264", "disposition": { "attached_pic": 0 } }
                  ]
                }
                """;

            var metadata = Map(json);

            Assert.True(metadata.HasAudioStream);
            Assert.True(metadata.HasVideoStream);
            Assert.False(FileUtils.IsProbedAudioContent(metadata));
        }

        // The control that separates cover art from real video. Identical to the rejected case
        // above except the video stream carries disposition.attached_pic = 1. That single field
        // is the whole reason a normal audiobook with embedded artwork is admitted rather than
        // mistaken for a video file. If attached_pic were ignored this would reject and the fix
        // would refuse most real audiobooks.
        [Fact]
        [Trait("Method", "Map")]
        public void ContainerWithAudioAndAttachedCoverArt_IsAdmitted()
        {
            const string json = """
                {
                  "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "3600.0" },
                  "streams": [
                    { "codec_type": "audio", "codec_name": "aac", "channels": 2, "sample_rate": "44100" },
                    { "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } }
                  ]
                }
                """;

            var metadata = Map(json);

            Assert.True(metadata.HasAudioStream);
            Assert.False(metadata.HasVideoStream);
            Assert.True(FileUtils.IsProbedAudioContent(metadata));
        }

        [Fact]
        [Trait("Method", "Map")]
        public void VideoOnlyContainer_IsRejected()
        {
            const string json = """
                {
                  "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "3600.0" },
                  "streams": [
                    { "codec_type": "video", "codec_name": "h264", "disposition": { "attached_pic": 0 } }
                  ]
                }
                """;

            var metadata = Map(json);

            Assert.False(metadata.HasAudioStream);
            Assert.True(metadata.HasVideoStream);
            Assert.False(FileUtils.IsProbedAudioContent(metadata));
        }

        // An unreadable or empty container probes to no streams. It must be rejected, which is
        // what keeps the empty .mp4 fixtures in DownloadProcessingJobProcessorTests blocking
        // rather than importing garbage.
        [Fact]
        [Trait("Method", "Map")]
        public void ContainerWithNoStreams_IsRejected()
        {
            const string json = """
                { "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2" }, "streams": [] }
                """;

            var metadata = Map(json);

            Assert.False(metadata.HasAudioStream);
            Assert.False(metadata.HasVideoStream);
            Assert.False(FileUtils.IsProbedAudioContent(metadata));
        }
    }
}
