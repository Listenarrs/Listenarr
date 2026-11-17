using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// FfmpegService runs <c>ffprobe -version</c> on the binary it resolved before using it, and a
    /// packaged binary that fails is reported as unavailable rather than replaced by a copy in the
    /// configured ffmpeg directory.
    /// </summary>
    [Trait("Name", "FfmpegServiceValidationTests")]
    [Trait("Category", "FfmpegService")]
    public class FfmpegServiceValidationTests : BaseTests
    {
        private static string ExecutableName => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

        [Fact]
        public async Task GetFfprobePathAsync_PackagedBinary_IsCheckedWithVersionAndAFiveSecondTimeout()
        {
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync("ffprobe-validate-app");
            var calls = new List<(ProcessStartInfo StartInfo, int TimeoutMs)>();
            var processRunner = new Mock<IProcessRunner>();
            processRunner.Setup(runner => runner.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<ProcessStartInfo, int, CancellationToken>((startInfo, timeoutMs, _) => calls.Add((startInfo, timeoutMs)))
                .ReturnsAsync(new ProcessResult(0, FfprobeProcessRunnerMockExtensions.VersionOutput, string.Empty, false));
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-validate-legacy"), applicationBase);

            Assert.Equal(packaged, await service.GetFfprobePathAsync());

            var (startInfo, timeoutMs) = Assert.Single(calls);
            Assert.Equal(Path.GetFullPath(packaged), startInfo.FileName);
            Assert.Equal(["-version"], startInfo.ArgumentList);
            Assert.Equal(5000, timeoutMs);
        }

        [Theory]
        [InlineData(1, "ffprobe version 9.0.2", false)]
        [InlineData(-1, "", true)]
        [InlineData(0, "ffprobe version 9.0.2", true)]
        [InlineData(0, "ffmpeg version 9.0.2", false)]
        [InlineData(0, "", false)]
        [InlineData(0, "warning: something\nffprobe version 9.0.2", false)]
        public async Task PackagedBinaryFailingTheCheck_IsUnavailableAndTheLegacyCopyIsNotUsed(int exitCode, string stdout, bool timedOut)
        {
            var (applicationBase, _) = await PlacePackagedBinaryAsync("ffprobe-invalid-app");
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-invalid-legacy");
            var legacy = Path.Join(legacyDirectory, ExecutableName);
            await File.WriteAllTextAsync(legacy, "downloaded by an older release");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(exitCode, stdout, string.Empty, timedOut));
            var requests = new List<Uri?>();
            var service = CreateService(processRunner.Object, legacyDirectory, applicationBase, requests);

            Assert.Null(await service.GetFfprobePathAsync());
            Assert.Null(await service.EnsureFfprobeInstalledAsync());

            Assert.Empty(requests);
            Assert.Equal("downloaded by an older release", await File.ReadAllTextAsync(legacy));
        }

        [Fact]
        public async Task PackagedBinaryThatCannotBeStarted_IsUnavailable()
        {
            var (applicationBase, _) = await PlacePackagedBinaryAsync("ffprobe-unstartable-app");
            var processRunner = new Mock<IProcessRunner>();
            processRunner.Setup(runner => runner.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Win32Exception("Exec format error"));
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-unstartable-legacy"), applicationBase);

            Assert.Null(await service.EnsureFfprobeInstalledAsync());
        }

        [Fact]
        public async Task ValidPackagedBinary_LeavesTheLegacyCopyOnDiskUntouched()
        {
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync("ffprobe-coexist-app");
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-coexist-legacy");
            var legacy = Path.Join(legacyDirectory, ExecutableName);
            await File.WriteAllTextAsync(legacy, "downloaded by an older release");
            var service = CreateService(new Mock<IProcessRunner>().AnswerFfprobeVersionCheck().Object, legacyDirectory, applicationBase);

            Assert.Equal(packaged, await service.EnsureFfprobeInstalledAsync());

            Assert.Equal("downloaded by an older release", await File.ReadAllTextAsync(legacy));
        }

        [Fact]
        public async Task LegacyOnlyBinaryFailingTheCheck_IsUnavailableAndNotReplacedByADownload()
        {
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-invalid");
            await File.WriteAllTextAsync(Path.Join(legacyDirectory, ExecutableName), "truncated");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(1, string.Empty, "exec format error", false));
            var requests = new List<Uri?>();
            var service = CreateService(processRunner.Object, legacyDirectory, FileService.GetTempDirectory("ffprobe-legacy-invalid-app"), requests);

            Assert.Null(await service.EnsureFfprobeInstalledAsync());
            Assert.Empty(requests);
        }

        [Fact]
        public async Task VersionCheck_IsCachedUntilTheFileChanges()
        {
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync("ffprobe-cache-app");
            var processRunner = new Mock<IProcessRunner>().AnswerFfprobeVersionCheck();
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-cache-legacy"), applicationBase);

            await service.GetFfprobePathAsync();
            await service.GetFfprobePathAsync();
            VerifyVersionChecks(processRunner, 1);

            await File.WriteAllTextAsync(packaged, "packaged, replaced by a longer build");
            await service.GetFfprobePathAsync();
            VerifyVersionChecks(processRunner, 2);

            File.SetLastWriteTimeUtc(packaged, File.GetLastWriteTimeUtc(packaged).AddMinutes(-5));
            await service.GetFfprobePathAsync();
            VerifyVersionChecks(processRunner, 3);
        }

        [LinuxFact]
        [SupportedOSPlatform("linux")]
        public async Task VersionCheck_RerunsWhenOnlyTheModeChanges()
        {
            // chmod changes the mode but not the length or the modification time (the execute-bit
            // repair is one such change), so a cached result must not outlive it.
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync("ffprobe-mode-cache-app");
            var processRunner = new Mock<IProcessRunner>().AnswerFfprobeVersionCheck();
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-mode-cache-legacy"), applicationBase);
            Assert.Equal(packaged, await service.GetFfprobePathAsync());
            Assert.Equal(packaged, await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 1);
            var lastWrite = File.GetLastWriteTimeUtc(packaged);

            File.SetUnixFileMode(packaged, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(packaged));
            await service.GetFfprobePathAsync();
            VerifyVersionChecks(processRunner, 2);
        }

        [Theory]
        [InlineData(-1, "", true)]
        [InlineData(-1, "resource temporarily unavailable", false)]
        public async Task VersionCheck_TimeoutOrRunnerFailure_IsNotCachedSoTheNextCallSucceeds(int exitCode, string stderr, bool timedOut)
        {
            // A timeout, or a process the runner could not start (exit -1 and no output), says
            // nothing lasting about the binary, so the next caller checks again instead of
            // treating ffprobe as unusable until restart.
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync($"ffprobe-transient-app-{timedOut}");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(exitCode, string.Empty, stderr, timedOut));
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory($"ffprobe-transient-legacy-{timedOut}"), applicationBase);
            Assert.Null(await service.GetFfprobePathAsync());

            processRunner.AnswerFfprobeVersionCheck();

            Assert.Equal(packaged, await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 2);
        }

        [Fact]
        public async Task VersionCheck_RunnerThrowing_IsNotCachedSoTheNextCallSucceeds()
        {
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync("ffprobe-runner-throws-app");
            var processRunner = new Mock<IProcessRunner>();
            processRunner.SetupSequence(runner => runner.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Win32Exception("Text file busy"))
                .ReturnsAsync(new ProcessResult(0, FfprobeProcessRunnerMockExtensions.VersionOutput, string.Empty, false));
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-runner-throws-legacy"), applicationBase);
            Assert.Null(await service.GetFfprobePathAsync());

            Assert.Equal(packaged, await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 2);
        }

        [Fact]
        public async Task VersionCheck_FailureTheBinaryReports_StaysCachedUntilTheFileChanges()
        {
            // A non-zero exit or the wrong first line comes from the binary itself, so re-running
            // it on every probe would only repeat the same answer.
            var (applicationBase, _) = await PlacePackagedBinaryAsync("ffprobe-reported-failure-app");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(1, string.Empty, "unknown option", false));
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-reported-failure-legacy"), applicationBase);
            Assert.Null(await service.GetFfprobePathAsync());

            processRunner.AnswerFfprobeVersionCheck();

            Assert.Null(await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 1);
        }

        [Fact]
        public async Task RunFfprobeAsync_PackagedBinaryFailingTheCheck_ThrowsWithTheReasonAndNeverProbes()
        {
            var (applicationBase, _) = await PlacePackagedBinaryAsync("ffprobe-run-invalid-app");
            var target = await FileService.GetFileAsync(FileService.GetTempDirectory("ffprobe-run-invalid-target"), "Chapter.m4b", "audio");
            var processRunner = new Mock<IProcessRunner>(MockBehavior.Strict)
                .AnswerFfprobeVersionCheck(new ProcessResult(1, string.Empty, string.Empty, false));
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-run-invalid-legacy"), applicationBase);

            var exception = await Assert.ThrowsAsync<FfmpegException>(() => service.RunFfprobeAsync(target));

            // RunFfprobeAsync wraps every failure in a per-file message; the reason is the inner one.
            Assert.Contains("-version exited with code 1", exception.InnerException?.Message);
            VerifyVersionChecks(processRunner, 1);
        }

        private static void VerifyVersionChecks(Mock<IProcessRunner> processRunner, int expected)
        {
            processRunner.Verify(
                runner => runner.RunAsync(
                    It.Is<ProcessStartInfo>(startInfo => FfprobeProcessRunnerMockExtensions.IsVersionCheck(startInfo)),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(expected));
        }

        private async Task<(string ApplicationBase, string Packaged)> PlacePackagedBinaryAsync(string name)
        {
            var applicationBase = FileService.GetTempDirectory(name);
            var packaged = Path.Join(applicationBase, ExecutableName);
            await File.WriteAllTextAsync(packaged, "packaged");
            return (applicationBase, packaged);
        }

        private FfmpegService CreateService(
            IProcessRunner processRunner,
            string legacyDirectory,
            string applicationBaseDirectory,
            List<Uri?>? requests = null)
        {
            var recorded = requests ?? [];
            return new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(new DelegatingHandlerMock((request, _) =>
                {
                    recorded.Add(request.RequestUri);
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
                })),
                _provider.GetRequiredService<IStartupConfigService>(),
                processRunner,
                Mock.Of<IApplicationPathService>(service => service.FfmpegRootPath == legacyDirectory),
                applicationBaseDirectory,
                RuntimeInformation.RuntimeIdentifier);
        }
    }
}
