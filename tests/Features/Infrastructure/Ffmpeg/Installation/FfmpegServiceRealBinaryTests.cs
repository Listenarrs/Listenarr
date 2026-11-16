using System.Diagnostics;
using System.Runtime.InteropServices;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// Runs the ffprobe binary restored from the Openur.FFprobeStatic package on whatever platform
    /// the suite runs on (both the Linux and the Windows CI jobs run the full suite), so a package
    /// bump that ships a binary which does not start, reports a different first line or maps tags
    /// differently fails here rather than in a user's library scan.
    /// </summary>
    [Trait("Name", "FfmpegServiceRealBinaryTests")]
    [Trait("Category", "FfmpegService")]
    public class FfmpegServiceRealBinaryTests : BaseTests
    {
        private const string TaggedFixtureName = "the-time-machine-silence.m4b";

        [Fact]
        public async Task PackagedBinary_PassesTheVersionCheckAndTheNoticeNamesItsVersion()
        {
            var service = CreateService();

            var path = await service.EnsureFfprobeInstalledAsync();

            Assert.NotNull(path);
            // Read the first line independently of the service, so the notice is compared with
            // what the binary prints rather than with the service's own parse of it.
            var firstLine = await ReadVersionLineAsync(path);
            Assert.StartsWith("ffprobe version ", firstLine);
            var version = firstLine["ffprobe version ".Length..].Split(' ')[0];
            Assert.NotEmpty(version);
            Assert.StartsWith($"ffprobe {version} is a static FFmpeg build", await service.GetLicenseAsync());
        }

        [Fact]
        public async Task PackagedBinary_ProbesATaggedAudiobook_AndTheMapperReadsItsTags()
        {
            var service = CreateService();
            Assert.NotNull(await service.EnsureFfprobeInstalledAsync());
            var path = Path.Join(FileService.GetTempDirectory("ffprobe-real-tagged"), "book.m4b");
            File.Copy(TestUtils.GetAudioDataPath(TaggedFixtureName), path);

            var metadata = await service.RunFfprobeAsync(path);

            // The fixture's tags and stream shape are documented in TagLibAudioTagWriterRoundTripTests.
            Assert.Equal("The Time Machine", metadata.Title);
            Assert.Equal("H. G. Wells", metadata.Artist);
            Assert.Equal("aac", metadata.Codec);
            Assert.Equal(22050, metadata.SampleRate);
            Assert.Equal(1, metadata.Channels);
            Assert.InRange(metadata.Duration.TotalSeconds, 0.9, 1.1);
        }

        private FfmpegService CreateService()
        {
            return new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(new DelegatingHandlerMock((_, _) => throw new InvalidOperationException("No test may reach a vendor host"))),
                _provider.GetRequiredService<IStartupConfigService>(),
                new UnredactedProcessRunner(),
                Mock.Of<IApplicationPathService>(service => service.FfmpegRootPath == Path.Join(FileService.GetTempPath(), "ffmpeg-real-unused")),
                applicationBaseDirectory: null,
                RuntimeInformation.RuntimeIdentifier);
        }

        private static async Task<string> ReadVersionLineAsync(string path)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-version");
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("ffprobe did not start");
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            return output.Split('\n', 2)[0].Trim();
        }
    }
}
