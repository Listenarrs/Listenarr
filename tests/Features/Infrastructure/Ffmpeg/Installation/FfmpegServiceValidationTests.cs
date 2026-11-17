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
        public async Task PackagedBinaryFailingTheCheck_IsUnavailableAndTheLegacyCopyStaysOnDisk(int exitCode, string stdout, bool timedOut)
        {
            var (applicationBase, _) = await PlacePackagedBinaryAsync("ffprobe-invalid-app");
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-invalid-legacy");
            var legacy = Path.Join(legacyDirectory, ExecutableName);
            await File.WriteAllTextAsync(legacy, "downloaded by an older release");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(exitCode, stdout, string.Empty, timedOut));
            var service = CreateService(processRunner.Object, legacyDirectory, applicationBase);

            Assert.Null(await service.GetFfprobePathAsync());
            Assert.Null(await service.EnsureFfprobeInstalledAsync());

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
        public async Task LegacyOnlyBinaryFailingTheCheck_IsUnavailableAndLeftInPlace()
        {
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-invalid");
            var legacy = Path.Join(legacyDirectory, ExecutableName);
            await File.WriteAllTextAsync(legacy, "truncated");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(1, string.Empty, "exec format error", false));
            var service = CreateService(processRunner.Object, legacyDirectory, FileService.GetTempDirectory("ffprobe-legacy-invalid-app"));

            Assert.Null(await service.EnsureFfprobeInstalledAsync());
            Assert.Equal([ExecutableName], Directory.EnumerateFileSystemEntries(legacyDirectory).Select(Path.GetFileName));
            Assert.Equal("truncated", await File.ReadAllTextAsync(legacy));
        }

        [Fact]
        public async Task VersionCheck_IsCachedUntilTheFileChanges()
        {
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync("ffprobe-cache-app");
            var processRunner = new Mock<IProcessRunner>().AnswerFfprobeVersionCheck();
            var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-cache-legacy"), applicationBase, clock);

            await service.GetFfprobePathAsync();
            // A success has no time limit, unlike a held timeout or start failure.
            clock.Advance(TimeSpan.FromDays(1));
            await service.GetFfprobePathAsync();
            VerifyVersionChecks(processRunner, 1);

            // Only the length changes here: the modification time is put back after the rewrite.
            var lastWrite = File.GetLastWriteTimeUtc(packaged);
            await File.WriteAllTextAsync(packaged, "packaged, replaced by a longer build");
            File.SetLastWriteTimeUtc(packaged, lastWrite);
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
        public async Task VersionCheck_TimeoutOrRunnerFailure_IsHeldForSixtySecondsThenCheckedAgain(int exitCode, string stderr, bool timedOut)
        {
            // A timeout, or a process the runner could not start (exit -1 and no output), says
            // nothing lasting about the binary: it is held briefly so a binary that never starts
            // is not re-run on every probe, then checked again instead of staying unusable until
            // restart.
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync($"ffprobe-transient-app-{timedOut}");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(exitCode, string.Empty, stderr, timedOut));
            var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory($"ffprobe-transient-legacy-{timedOut}"), applicationBase, clock);
            Assert.Null(await service.GetFfprobePathAsync());
            processRunner.AnswerFfprobeVersionCheck();

            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.Null(await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 1);

            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(packaged, await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 2);
        }

        [Fact]
        public async Task VersionCheck_RunnerThrowing_IsHeldForSixtySecondsThenCheckedAgain()
        {
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync("ffprobe-runner-throws-app");
            var processRunner = new Mock<IProcessRunner>();
            processRunner.SetupSequence(runner => runner.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Win32Exception("Text file busy"))
                .ReturnsAsync(new ProcessResult(0, FfprobeProcessRunnerMockExtensions.VersionOutput, string.Empty, false));
            var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-runner-throws-legacy"), applicationBase, clock);
            Assert.Null(await service.GetFfprobePathAsync());

            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.Null(await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 1);

            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(packaged, await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 2);
        }

        [Fact]
        public async Task VersionCheck_BinaryThatNeverStarts_WarnsOnceAndLogsRepeatsAtDebug()
        {
            var (applicationBase, _) = await PlacePackagedBinaryAsync("ffprobe-never-starts-app");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(-1, string.Empty, "permission denied", false));
            var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
            var logger = new LevelRecordingLogger();
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-never-starts-legacy"), applicationBase, clock, logger);

            for (var call = 0; call < 5; call++)
            {
                Assert.Null(await service.GetFfprobePathAsync());
            }

            VerifyVersionChecks(processRunner, 1);
            clock.Advance(TimeSpan.FromSeconds(61));
            Assert.Null(await service.GetFfprobePathAsync());

            VerifyVersionChecks(processRunner, 2);
            Assert.Equal([LogLevel.Warning, LogLevel.Debug], logger.Levels.Where(level => level is LogLevel.Warning or LogLevel.Debug));
        }

        [Fact]
        public async Task VersionCheck_DifferentFailureAfterTheHold_IsWarnedAboutAgain()
        {
            // Only a repeat of the same held failure drops to Debug; a new reason is news.
            var (applicationBase, _) = await PlacePackagedBinaryAsync("ffprobe-new-reason-app");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(-1, string.Empty, string.Empty, true));
            var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
            var logger = new LevelRecordingLogger();
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-new-reason-legacy"), applicationBase, clock, logger);
            Assert.Null(await service.GetFfprobePathAsync());
            processRunner.AnswerFfprobeVersionCheck(new ProcessResult(-1, string.Empty, "permission denied", false));

            clock.Advance(RetryableFailureWindowAndThenSome);
            Assert.Null(await service.GetFfprobePathAsync());

            Assert.Equal([LogLevel.Warning, LogLevel.Warning], logger.Levels.Where(level => level is LogLevel.Warning or LogLevel.Debug));
        }

        [Fact]
        public async Task VersionCheck_ReplacedBinaryFailingTheSameWay_IsWarnedAboutAgain()
        {
            // A failure the binary reports is only checked again once the file changes, and a
            // changed file that still fails is worth a warning, not a Debug line.
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync("ffprobe-replaced-failing-app");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(1, string.Empty, "unknown option", false));
            var logger = new LevelRecordingLogger();
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-replaced-failing-legacy"), applicationBase, logger: logger);
            Assert.Null(await service.GetFfprobePathAsync());

            await File.WriteAllTextAsync(packaged, "packaged, replaced by another broken build");
            Assert.Null(await service.GetFfprobePathAsync());

            VerifyVersionChecks(processRunner, 2);
            Assert.Equal([LogLevel.Warning, LogLevel.Warning], logger.Levels.Where(level => level is LogLevel.Warning or LogLevel.Debug));
        }

        [Theory]
        [InlineData(1, "", "unknown option")]
        [InlineData(0, "ffmpeg version 9.0.2 Copyright (c) 2000-2026 the FFmpeg developers\n", "")]
        public async Task VersionCheck_FailureTheBinaryReports_StaysCachedUntilTheFileChanges(int exitCode, string stdout, string stderr)
        {
            // A non-zero exit other than -1, or the wrong first line, comes from the binary itself,
            // so it is not held for a while like a timeout: it stays until the file changes.
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync($"ffprobe-reported-failure-app-{exitCode}");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(exitCode, stdout, stderr, false));
            var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory($"ffprobe-reported-failure-legacy-{exitCode}"), applicationBase, clock);
            Assert.Null(await service.GetFfprobePathAsync());
            processRunner.AnswerFfprobeVersionCheck();

            clock.Advance(RetryableFailureWindowAndThenSome);
            Assert.Null(await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 1);

            await File.WriteAllTextAsync(packaged, "packaged, replaced by a working build");
            Assert.Equal(packaged, await service.GetFfprobePathAsync());
            VerifyVersionChecks(processRunner, 2);
        }

        [LinuxFact]
        [SupportedOSPlatform("linux")]
        public async Task VersionCheck_HeldStartFailure_IsCheckedAgainWhenTheExecuteBitRepairChangesTheMode()
        {
            // A held failure is keyed like any other result, so the startup repair of the execute
            // bits ends the hold at once instead of after the window.
            var (applicationBase, packaged) = await PlacePackagedBinaryAsync("ffprobe-held-repair-app");
            File.SetUnixFileMode(packaged, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(-1, string.Empty, "permission denied", false));
            var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
            var service = CreateService(processRunner.Object, FileService.GetTempDirectory("ffprobe-held-repair-legacy"), applicationBase, clock);
            Assert.Null(await service.GetFfprobePathAsync());
            processRunner.AnswerFfprobeVersionCheck();

            Assert.Equal(packaged, await service.EnsureFfprobeInstalledAsync());
            VerifyVersionChecks(processRunner, 2);
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

        [Fact]
        public async Task GetFfprobePathAsync_PackagedBinaryFailing_DoesNotFallBackToAWorkingLegacyCopy()
        {
            var (service, processRunner, legacy) = await PlaceFailingPackagedAndWorkingLegacyAsync("ffprobe-no-fallback-get");

            Assert.Null(await service.GetFfprobePathAsync());

            VerifyNeverRun(processRunner, legacy);
        }

        [Fact]
        public async Task EnsureFfprobeInstalledAsync_PackagedBinaryFailing_DoesNotFallBackToAWorkingLegacyCopy()
        {
            var (service, processRunner, legacy) = await PlaceFailingPackagedAndWorkingLegacyAsync("ffprobe-no-fallback-ensure");

            Assert.Null(await service.EnsureFfprobeInstalledAsync());

            VerifyNeverRun(processRunner, legacy);
        }

        [Fact]
        public async Task RunFfprobeAsync_PackagedBinaryFailing_DoesNotFallBackToAWorkingLegacyCopy()
        {
            var (service, processRunner, legacy) = await PlaceFailingPackagedAndWorkingLegacyAsync("ffprobe-no-fallback-run");
            var target = await FileService.GetFileAsync(FileService.GetTempDirectory("ffprobe-no-fallback-run-target"), "Chapter.m4b", "audio");

            var exception = await Assert.ThrowsAsync<FfmpegException>(() => service.RunFfprobeAsync(target));

            Assert.Contains("-version exited with code 1", exception.InnerException?.Message);
            VerifyNeverRun(processRunner, legacy);
        }

        /// <summary>
        /// A packaged binary that fails its check next to a legacy copy that would pass it. The
        /// runner answers per path, so a fallback to the legacy copy would succeed and be seen.
        /// </summary>
        private async Task<(FfmpegService Service, Mock<IProcessRunner> ProcessRunner, string Legacy)> PlaceFailingPackagedAndWorkingLegacyAsync(string name)
        {
            var (applicationBase, _) = await PlacePackagedBinaryAsync($"{name}-app");
            var legacyDirectory = FileService.GetTempDirectory($"{name}-legacy");
            var legacy = Path.GetFullPath(Path.Join(legacyDirectory, ExecutableName));
            await File.WriteAllTextAsync(legacy, "downloaded by an older release");
            var processRunner = new Mock<IProcessRunner>()
                .AnswerFfprobeVersionCheck(new ProcessResult(1, string.Empty, "exec format error", false));
            processRunner.Setup(runner => runner.RunAsync(
                    It.Is<ProcessStartInfo>(startInfo => startInfo.FileName == legacy),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessResult(0, FfprobeProcessRunnerMockExtensions.VersionOutput, string.Empty, false));
            return (CreateService(processRunner.Object, legacyDirectory, applicationBase), processRunner, legacy);
        }

        private static void VerifyNeverRun(Mock<IProcessRunner> processRunner, string path)
        {
            processRunner.Verify(
                runner => runner.RunAsync(
                    It.Is<ProcessStartInfo>(startInfo => startInfo.FileName == path),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        private static readonly TimeSpan RetryableFailureWindowAndThenSome = FfmpegService.RetryableFailureCacheDuration + TimeSpan.FromMinutes(1);

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
            TimeProvider? timeProvider = null,
            ILogger<FfmpegService>? logger = null)
        {
            return new FfmpegService(
                logger ?? new Mock<ILogger<FfmpegService>>().Object,
                processRunner,
                Mock.Of<IApplicationPathService>(service => service.FfmpegRootPath == legacyDirectory),
                applicationBaseDirectory,
                RuntimeInformation.RuntimeIdentifier,
                timeProvider);
        }

        private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
        {
            private DateTimeOffset _utcNow = utcNow;

            public override DateTimeOffset GetUtcNow() => _utcNow;

            public void Advance(TimeSpan duration) => _utcNow += duration;
        }

        private sealed class LevelRecordingLogger : ILogger<FfmpegService>
        {
            public List<LogLevel> Levels { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Levels.Add(logLevel);
            }
        }
    }
}
