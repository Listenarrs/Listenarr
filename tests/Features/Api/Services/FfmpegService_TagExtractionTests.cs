using System.Diagnostics;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "FfmpegService_TagExtractionTests")]
    [Trait("Category", "FfmpegService")]
    public class FfmpegService_TagExtractionTests
    {
        private static FfmpegService BuildService(string ffprobeJson, out string fakeFilePath)
        {
            fakeFilePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.m4b");

            var runner = new Mock<IProcessRunner>(MockBehavior.Strict);
            runner
                .Setup(r => r.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessResult(0, ffprobeJson, string.Empty, false));

            var startup = new Mock<IStartupConfigService>();
            startup.Setup(s => s.GetConfig()).Returns(new StartupConfig());

            return new FfmpegService(NullLogger<FfmpegService>.Instance, startup.Object, runner.Object);
        }

        [Fact]
        public async Task RunFfprobeAsync_ExtractsAsin_FromAppleITunesDashBoxTag()
        {
            const string json = """
            {
              "format": {
                "duration": "3600.5",
                "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
                "bit_rate": "128000",
                "tags": {
                  "title": "Mistborn",
                  "album_artist": "Brandon Sanderson",
                  "composer": "Michael Kramer",
                  "----:com.apple.iTunes:ASIN": "B002UZHDC0",
                  "publisher": "Tor",
                  "language": "eng"
                }
              },
              "streams": [
                { "codec_type": "audio", "sample_rate": "44100", "channels": 2, "codec_name": "aac" }
              ]
            }
            """;

            var service = BuildService(json, out var path);
            var meta = await service.RunFfprobeAsync(path);

            Assert.Equal("B002UZHDC0", meta.Asin);
            Assert.Equal("Mistborn", meta.Title);
            Assert.Equal("Brandon Sanderson", meta.AlbumArtist);
            Assert.Equal("Michael Kramer", meta.Narrator);
            Assert.Equal("Tor", meta.Publisher);
            Assert.Equal("eng", meta.Language);
        }

        [Fact]
        public async Task RunFfprobeAsync_NormalizesAsinFromCDEKTag()
        {
            const string json = """
            {
              "format": {
                "tags": {
                  "CDEK": "Some prefix B0ABCDEFGH some suffix"
                }
              },
              "streams": [ { "codec_type": "audio", "channels": 2 } ]
            }
            """;

            var service = BuildService(json, out var path);
            var meta = await service.RunFfprobeAsync(path);

            Assert.Equal("B0ABCDEFGH", meta.Asin);
        }

        [Fact]
        public async Task RunFfprobeAsync_ExtractsIsbnAndStripsHyphens()
        {
            const string json = """
            {
              "format": {
                "tags": {
                  "ISBN": "978-0-7653-1178-8"
                }
              },
              "streams": [ { "codec_type": "audio", "channels": 2 } ]
            }
            """;

            var service = BuildService(json, out var path);
            var meta = await service.RunFfprobeAsync(path);

            Assert.Equal("9780765311788", meta.Isbn);
        }

        [Fact]
        public async Task RunFfprobeAsync_ExtractsSeriesAndPart_AsDecimal()
        {
            const string json = """
            {
              "format": {
                "tags": {
                  "SERIES": "The Stormlight Archive",
                  "PART": "1"
                }
              },
              "streams": [ { "codec_type": "audio", "channels": 2 } ]
            }
            """;

            var service = BuildService(json, out var path);
            var meta = await service.RunFfprobeAsync(path);

            Assert.Equal("The Stormlight Archive", meta.Series);
            Assert.Equal(1m, meta.SeriesPosition);
        }

        [Fact]
        public async Task RunFfprobeAsync_CapsDescriptionAt2000Chars()
        {
            var longDesc = new string('x', 3000);
            var json = $$"""
            {
              "format": {
                "tags": {
                  "description": "{{longDesc}}"
                }
              },
              "streams": [ { "codec_type": "audio", "channels": 2 } ]
            }
            """;

            var service = BuildService(json, out var path);
            var meta = await service.RunFfprobeAsync(path);

            Assert.NotNull(meta.Description);
            Assert.Equal(2000, meta.Description!.Length);
        }

        [Fact]
        public async Task RunFfprobeAsync_DetectsAttachedPicVideoStream()
        {
            const string json = """
            {
              "format": { "tags": { "title": "Cover Test" } },
              "streams": [
                { "codec_type": "audio", "channels": 2 },
                {
                  "codec_type": "video",
                  "codec_name": "mjpeg",
                  "index": 1,
                  "disposition": { "attached_pic": 1 }
                }
              ]
            }
            """;

            var service = BuildService(json, out var path);
            var meta = await service.RunFfprobeAsync(path);

            Assert.True(meta.AdditionalData.ContainsKey("AttachedPicCodec"));
            Assert.Equal("mjpeg", meta.AdditionalData["AttachedPicCodec"]);
        }

        [Fact]
        public async Task RunFfprobeAsync_DoesNotFlagAttachedPic_WhenDispositionMissing()
        {
            const string json = """
            {
              "format": { "tags": { "title": "No Cover" } },
              "streams": [
                { "codec_type": "audio", "channels": 2 },
                { "codec_type": "video", "codec_name": "h264", "index": 1 }
              ]
            }
            """;

            var service = BuildService(json, out var path);
            var meta = await service.RunFfprobeAsync(path);

            Assert.False(meta.AdditionalData.ContainsKey("AttachedPicCodec"));
        }
    }
}
