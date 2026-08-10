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
        public async Task RunFfprobeAsync_ExtensionlessStablePath_UsesPublicIdentityForValidationAndMapping()
        {
            var ffmpegDirectory = FileService.GetTempDirectory("ffprobe-root");
            var ffprobePath = Path.Join(
                ffmpegDirectory,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            await File.WriteAllTextAsync(ffprobePath, "fake");
            var stableReadPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("ffprobe-stable-target"),
                "42",
                "audio");
            var publicPath = Path.Join("library", "Public.Name.M4B");
            System.Diagnostics.ProcessStartInfo? capturedStartInfo = null;
            var processRunner = new Mock<IProcessRunner>();
            processRunner.Setup(runner => runner.RunAsync(
                    It.IsAny<System.Diagnostics.ProcessStartInfo>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<System.Diagnostics.ProcessStartInfo, int, CancellationToken>(
                    (startInfo, _, _) => capturedStartInfo = startInfo)
                .ReturnsAsync(new ProcessResult(
                    0,
                    "{\"format\":{\"format_name\":\"mov\",\"duration\":\"1\"},\"streams\":[]}",
                    string.Empty,
                    false));
            var service = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(),
                _provider.GetRequiredService<IStartupConfigService>(),
                processRunner.Object,
                Mock.Of<IApplicationPathService>(applicationPathService =>
                    applicationPathService.FfmpegRootPath == ffmpegDirectory));

            var metadata = await service.RunFfprobeAsync(
                new MetadataFileSource(stableReadPath, publicPath));

            Assert.NotNull(capturedStartInfo);
            Assert.Equal(
                Path.GetFullPath(stableReadPath),
                capturedStartInfo.ArgumentList[^1]);
            Assert.Equal("Public.Name", metadata.Title);
            Assert.Equal("M4B", metadata.Format);
            Assert.Equal("M4B", metadata.Container);
        }

        [LinuxFact]
        public async Task RunFfprobeAsync_LinuxPinnedDescriptor_ReadsStableBytesAndMapsPublicIdentity()
        {
            var source = await FileService.GetFileAsync(
                FileService.GetTempDirectory("ffprobe-linux-source"),
                "Source.m4b",
                "audio");
            var destination = Path.Join(
                FileService.GetTempDirectory("ffprobe-linux-destination"),
                "Public.Name.M4B");
            var movedDestination = Path.Join(
                Path.GetDirectoryName(destination)!,
                "renamed-after-lease.bin");
            var mover = _provider.GetRequiredService<FileMover>();
            using var lease = await mover.PrepareActionForRegistrationAsync(
                FileAction.Copy,
                source,
                destination,
                Guid.NewGuid());
            Assert.NotNull(lease);
            File.Move(destination, movedDestination);

            var ffmpegDirectory = FileService.GetTempDirectory("ffprobe-root");
            var ffprobePath = Path.Join(ffmpegDirectory, "ffprobe");
            await File.WriteAllTextAsync(ffprobePath, "fake");
            System.Diagnostics.ProcessStartInfo? capturedStartInfo = null;
            var processRunner = new Mock<IProcessRunner>();
            processRunner.Setup(runner => runner.RunAsync(
                    It.IsAny<System.Diagnostics.ProcessStartInfo>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<System.Diagnostics.ProcessStartInfo, int, CancellationToken>(
                    (startInfo, _, _) => capturedStartInfo = startInfo)
                .ReturnsAsync(new ProcessResult(
                    0,
                    "{\"format\":{\"format_name\":\"mov\",\"duration\":\"1\"},\"streams\":[]}",
                    string.Empty,
                    false));
            var service = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(),
                _provider.GetRequiredService<IStartupConfigService>(),
                processRunner.Object,
                Mock.Of<IApplicationPathService>(applicationPathService =>
                    applicationPathService.FfmpegRootPath == ffmpegDirectory));

            var metadata = await service.RunFfprobeAsync(
                new MetadataFileSource(lease.MetadataPath, lease.PublicPath));

            Assert.NotNull(capturedStartInfo);
            Assert.Equal(
                Path.GetFullPath(lease.MetadataPath),
                capturedStartInfo.ArgumentList[^1]);
            Assert.Equal("Public.Name", metadata.Title);
            Assert.Equal("M4B", metadata.Format);
        }

        [Theory]
        [InlineData(1, false, "{\"format\":{}}")]
        [InlineData(-1, true, "{\"format\":{}}")]
        [InlineData(0, false, "")]
        [InlineData(0, false, "not-json")]
        public async Task RunFfprobeAsync_AnalyzerFailure_RejectsResult(
            int exitCode,
            bool timedOut,
            string stdout)
        {
            var ffmpegDirectory = FileService.GetTempDirectory("ffprobe-root");
            var ffprobePath = Path.Join(
                ffmpegDirectory,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            await File.WriteAllTextAsync(ffprobePath, "fake");
            var stableReadPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("ffprobe-failure-target"),
                "42",
                "audio");
            var processRunner = new Mock<IProcessRunner>();
            processRunner.Setup(runner => runner.RunAsync(
                    It.IsAny<System.Diagnostics.ProcessStartInfo>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessResult(
                    exitCode,
                    stdout,
                    string.Empty,
                    timedOut));
            var service = new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(),
                _provider.GetRequiredService<IStartupConfigService>(),
                processRunner.Object,
                Mock.Of<IApplicationPathService>(applicationPathService =>
                    applicationPathService.FfmpegRootPath == ffmpegDirectory));

            await Assert.ThrowsAsync<FfmpegException>(() =>
                service.RunFfprobeAsync(new MetadataFileSource(
                    stableReadPath,
                    Path.Join("library", "Public.Name.M4B"))));
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
