using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Installation
{
    [Trait("Name", "FfmpegServiceTests")]
    [Trait("Category", "FfmpegService")]
    public class FfmpegServiceTests : BaseTests
    {
        // FIXME: This is too longo for unit tests
        //[Fact]
        [Trait("Method", "EnsureFfprobeInstalledAsync")]
        [Trait("Category", "Release")]
        private async Task EnsureFfprobeInstalledAsync()
        {
            var ffmpegDirectory = Path.Combine(FileService.GetTempPath(), "ffmpeg");

            Assert.False(Path.Exists(ffmpegDirectory));

            var ffmpegService = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(),
                _provider.GetRequiredService<IStartupConfigService>(),
                _provider.GetRequiredService<IProcessRunner>(),
                Mock.Of<IApplicationPathService>(service => service.FfmpegRootPath == ffmpegDirectory));

            var ffprobePath = await ffmpegService.EnsureFfprobeInstalledAsync();

            Assert.NotNull(ffprobePath);
            Assert.True(Path.Exists(ffprobePath));
            Assert.True(Path.Exists(ffmpegDirectory));
        }

        [Fact]
        public async Task RunFfprobeAsync_RejectsNonAudioFileBeforeStartingProcess()
        {
            var ffmpegDirectory = FileService.GetTempDirectory("ffprobe-root");
            var ffprobePath = Path.Join(ffmpegDirectory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            await File.WriteAllTextAsync(ffprobePath, "fake");
            var textFile = await FileService.GetFileAsync(FileService.GetTempDirectory("ffprobe-target"), "notes.txt", "not audio");

            var processRunner = new Mock<IProcessRunner>();
            var service = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(),
                _provider.GetRequiredService<IStartupConfigService>(),
                processRunner.Object,
                Mock.Of<IApplicationPathService>(applicationPathService => applicationPathService.FfmpegRootPath == ffmpegDirectory));

            await Assert.ThrowsAsync<FfmpegException>(() => service.RunFfprobeAsync(textFile));
            processRunner.Verify(runner => runner.RunAsync(It.IsAny<System.Diagnostics.ProcessStartInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RunFfprobeAsync_RejectsMissingFileBeforeStartingProcess()
        {
            var ffmpegDirectory = FileService.GetTempDirectory("ffprobe-root");
            var ffprobePath = Path.Join(ffmpegDirectory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            await File.WriteAllTextAsync(ffprobePath, "fake");
            var missingFile = Path.Join(FileService.GetTempDirectory("ffprobe-target"), "missing.mp3");

            var processRunner = new Mock<IProcessRunner>();
            var service = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(),
                _provider.GetRequiredService<IStartupConfigService>(),
                processRunner.Object,
                Mock.Of<IApplicationPathService>(applicationPathService => applicationPathService.FfmpegRootPath == ffmpegDirectory));

            await Assert.ThrowsAsync<FfmpegException>(() => service.RunFfprobeAsync(missingFile));
            processRunner.Verify(runner => runner.RunAsync(It.IsAny<System.Diagnostics.ProcessStartInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
