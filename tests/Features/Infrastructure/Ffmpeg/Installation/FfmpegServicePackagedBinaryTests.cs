using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// ffprobe comes from the Openur.FFprobeStatic package restored at build time, so where the
    /// package covers the platform startup resolves a binary that is already on disk and never
    /// downloads one.
    /// </summary>
    [Trait("Name", "FfmpegServicePackagedBinaryTests")]
    [Trait("Category", "FfmpegService")]
    public class FfmpegServicePackagedBinaryTests : BaseTests
    {
        private static string ExecutableName => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

        [Fact]
        public async Task EnsureFfprobeInstalledAsync_PackagedBinary_ResolvesAndProbesRealAudio()
        {
            var legacyDirectory = Path.Join(FileService.GetTempPath(), "ffmpeg-legacy-unused");
            // A plain runner rather than SystemProcessRunner: that one regex-redacts every
            // process's stdout against process-wide secret values, and under the full parallel
            // suite another test's values corrupted the JSON. This test is about the binary.
            var service = CreateService(
                new UnredactedProcessRunner(),
                legacyDirectory,
                applicationBaseDirectory: null);

            var path = await service.EnsureFfprobeInstalledAsync();

            Assert.NotNull(path);
            Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(path));
            Assert.Equal(path, await service.GetFfprobePathAsync());

            // Run the packaged binary for real against a synthesized one-second WAV, through the
            // same RunFfprobeAsync path the scanner uses, so this proves the binary executes on
            // this platform and its output still maps.
            var wavPath = Path.Join(FileService.GetTempDirectory("ffprobe-real-audio"), "one-second.wav");
            await File.WriteAllBytesAsync(wavPath, BuildSilentWav(sampleRate: 8000, seconds: 1));

            var metadata = await service.RunFfprobeAsync(wavPath);

            Assert.InRange(metadata.Duration.TotalSeconds, 0.99, 1.01);
            Assert.Equal(8000, metadata.SampleRate);
            Assert.Equal(1, metadata.Channels);
            Assert.False(Directory.Exists(legacyDirectory));
        }

        [Fact]
        public async Task EnsureFfprobeInstalledAsync_PackagedBinaryPresent_NeverDownloads()
        {
            var applicationBase = FileService.GetTempDirectory("ffprobe-app-no-download");
            var packaged = Path.Join(applicationBase, ExecutableName);
            await File.WriteAllTextAsync(packaged, "packaged");
            var legacyDirectory = Path.Join(FileService.GetTempPath(), "ffmpeg-not-created");
            var handler = new RecordingHandler();
            var service = CreateService(new Mock<IProcessRunner>().Object, legacyDirectory, applicationBase, handler);

            Assert.Equal(packaged, await service.EnsureFfprobeInstalledAsync());
            Assert.Empty(handler.Requests);
            Assert.False(Directory.Exists(legacyDirectory));
        }

        [Fact]
        public async Task EnsureFfprobeInstalledAsync_NothingPackagedOrPresent_FallsBackToTheDownload()
        {
            // Platforms the package does not cover keep the first-boot download until it is
            // retired, so with nothing packaged and nothing hand-placed the installer runs.
            var emptyApplicationBase = FileService.GetTempDirectory("ffprobe-empty-app");
            var legacyDirectory = Path.Join(FileService.GetTempPath(), "ffmpeg-download-target");
            var handler = new RecordingHandler();
            var service = CreateService(new Mock<IProcessRunner>().Object, legacyDirectory, emptyApplicationBase, handler);

            Assert.Null(await service.EnsureFfprobeInstalledAsync());
            Assert.Single(handler.Requests);
            Assert.Empty(Directory.EnumerateFileSystemEntries(emptyApplicationBase));
        }

        [Fact]
        public void PackagedVersion_MatchesTheCentralPackageVersion()
        {
            var props = System.Xml.Linq.XDocument.Load(Path.Join(RepositoryRoot(), "Directory.Packages.props"));
            var version = props.Descendants("PackageVersion")
                .Single(element => (string?)element.Attribute("Include") == "Openur.FFprobeStatic")
                .Attribute("Version")?.Value;

            Assert.Equal(FfmpegService.PackagedVersion, version);
        }

        [Fact]
        public async Task GetLicenseAsync_NamesTheSourceOfTheBinaryInUse()
        {
            var applicationBase = FileService.GetTempDirectory("ffprobe-license-app");
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-license-legacy");
            var service = CreateService(new Mock<IProcessRunner>().Object, legacyDirectory, applicationBase);

            Assert.Equal(string.Empty, await service.GetLicenseAsync());

            await File.WriteAllTextAsync(Path.Join(legacyDirectory, ExecutableName), "placed by hand");
            Assert.Equal(FfmpegService.LegacyLicenseNotice, await service.GetLicenseAsync());

            await File.WriteAllTextAsync(Path.Join(applicationBase, ExecutableName), "packaged");
            Assert.Equal(FfmpegService.PackagedLicenseNotice, await service.GetLicenseAsync());
            Assert.Contains(FfmpegService.PackagedVersion, FfmpegService.PackagedLicenseNotice);
        }

        [Fact]
        public async Task GetFfprobePathAsync_PackagedAndLegacyBothPresent_PrefersPackaged()
        {
            var applicationBase = FileService.GetTempDirectory("ffprobe-app");
            var packaged = Path.Join(applicationBase, ExecutableName);
            await File.WriteAllTextAsync(packaged, "packaged");
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy");
            await File.WriteAllTextAsync(Path.Join(legacyDirectory, ExecutableName), "downloaded by an older release");
            var service = CreateService(new Mock<IProcessRunner>().Object, legacyDirectory, applicationBase);

            Assert.Equal(packaged, await service.GetFfprobePathAsync());
        }

        [Fact]
        public async Task GetFfprobePathAsync_OnlyLegacyPresent_FallsBackToIt()
        {
            var applicationBase = FileService.GetTempDirectory("ffprobe-app-empty");
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-only");
            var legacy = Path.Join(legacyDirectory, ExecutableName);
            await File.WriteAllTextAsync(legacy, "placed by hand");
            var service = CreateService(new Mock<IProcessRunner>().Object, legacyDirectory, applicationBase);

            Assert.Equal(legacy, await service.GetFfprobePathAsync());
        }

        [LinuxFact]
        [SupportedOSPlatform("linux")]
        public async Task EnsureFfprobeInstalledAsync_PackagedBinaryWithoutExecuteBit_IsMadeExecutable()
        {
            // Restore leaves owner-execute only, and an unzip tool or artifact transfer can strip
            // even that; the service restores the bits when it owns the file.
            var applicationBase = FileService.GetTempDirectory("ffprobe-mode");
            var packaged = Path.Join(applicationBase, "ffprobe");
            await File.WriteAllTextAsync(packaged, "packaged");
            File.SetUnixFileMode(packaged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var service = CreateService(
                new Mock<IProcessRunner>(MockBehavior.Strict).Object,
                Path.Join(FileService.GetTempPath(), "ffmpeg-unused"),
                applicationBase);

            Assert.Equal(packaged, await service.EnsureFfprobeInstalledAsync());

            var mode = File.GetUnixFileMode(packaged);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute), $"mode was {mode}");
            Assert.True(mode.HasFlag(UnixFileMode.GroupExecute), $"mode was {mode}");
            Assert.True(mode.HasFlag(UnixFileMode.OtherExecute), $"mode was {mode}");
        }

        private FfmpegService CreateService(
            IProcessRunner processRunner,
            string legacyDirectory,
            string? applicationBaseDirectory,
            HttpMessageHandler? httpHandler = null)
        {
            return new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new HttpClient(httpHandler ?? new RecordingHandler()),
                _provider.GetRequiredService<IStartupConfigService>(),
                processRunner,
                Mock.Of<IApplicationPathService>(service => service.FfmpegRootPath == legacyDirectory),
                applicationBaseDirectory,
                RuntimeInformation.RuntimeIdentifier);
        }

        private sealed class UnredactedProcessRunner : IProcessRunner
        {
            public async Task<ProcessResult> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, int timeoutMs = 60000, CancellationToken cancellationToken = default)
            {
                using var process = System.Diagnostics.Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Process did not start");
                var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(timeoutMs);
                await process.WaitForExitAsync(timeout.Token);
                return new ProcessResult(process.ExitCode, await stdout, await stderr, false);
            }

            public System.Diagnostics.Process StartProcess(System.Diagnostics.ProcessStartInfo startInfo)
            {
                throw new NotSupportedException();
            }

            public IDisposable RegisterTransientSensitive(IEnumerable<string> values)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>Answers every request with 404 and records it, so no test reaches a vendor host.</summary>
        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<Uri?> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Join(directory.FullName, "listenarr.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory.FullName;
        }

        private static byte[] BuildSilentWav(int sampleRate, int seconds)
        {
            const short channels = 1;
            const short bitsPerSample = 16;
            var dataLength = sampleRate * seconds * channels * (bitsPerSample / 8);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + dataLength);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * (bitsPerSample / 8));
            writer.Write((short)(channels * (bitsPerSample / 8)));
            writer.Write(bitsPerSample);
            writer.Write("data"u8.ToArray());
            writer.Write(dataLength);
            writer.Write(new byte[dataLength]);
            writer.Flush();
            return stream.ToArray();
        }
    }
}
