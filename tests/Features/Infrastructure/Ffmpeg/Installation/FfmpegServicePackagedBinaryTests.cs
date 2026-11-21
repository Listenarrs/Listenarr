using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// ffprobe comes from the Openur.FFprobeStatic package restored at build time, so startup
    /// resolves a binary that is already on disk and never downloads one.
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
        public async Task EnsureFfprobeInstalledAsync_NothingPackagedOrPresent_ReturnsNullAndTouchesNothing()
        {
            // Regression guard for the removed first-boot download: with no packaged binary and
            // no legacy one, startup used to create the ffmpeg directory and fetch an archive.
            // Now it reports absence and leaves the filesystem alone.
            var emptyApplicationBase = FileService.GetTempDirectory("ffprobe-empty-app");
            var legacyDirectory = Path.Join(FileService.GetTempPath(), "ffmpeg-never-created");
            var processRunner = new Mock<IProcessRunner>(MockBehavior.Strict);
            var service = CreateService(processRunner.Object, legacyDirectory, emptyApplicationBase);

            var path = await service.EnsureFfprobeInstalledAsync();

            Assert.Null(path);
            Assert.Null(await service.GetFfprobePathAsync());
            Assert.False(Directory.Exists(legacyDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(emptyApplicationBase));
        }

        [Fact]
        public void FfmpegTypes_TakeNoHttpClientDependency()
        {
            var networkTypes = new[] { typeof(HttpClient), typeof(IHttpClientFactory), typeof(HttpMessageHandler) };
            var offenders = typeof(FfmpegService).Assembly
                .GetTypes()
                .Where(type => type.Namespace?.StartsWith("Listenarr.Infrastructure.Ffmpeg", StringComparison.Ordinal) == true)
                .SelectMany(type => type
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
                    .Concat(type
                        .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .Select(field => field.FieldType))
                    .Where(memberType => networkTypes.Any(networkType => networkType.IsAssignableFrom(memberType)))
                    .Select(memberType => $"{type.FullName} -> {memberType.Name}"))
                .Distinct()
                .ToArray();

            Assert.True(
                offenders.Length == 0,
                $"ffprobe provisioning must not take an HTTP dependency: {string.Join(", ", offenders)}");
        }

        [Fact]
        public void FfmpegSources_ContainNoNetworkCalls()
        {
            // The reflection test above misses an HttpClient created inside a method body, so
            // scan the source for any network API as well.
            var forbidden = new Regex(@"\b(HttpClient|HttpMessageHandler|System\.Net\.Http|WebClient|WebRequest|Socket|SslStream)\b", RegexOptions.Compiled);
            var ffmpegRoot = Path.Join(TestUtils.FindRepositoryRoot(), "listenarr.infrastructure", "Ffmpeg");
            var offenders = Directory.EnumerateFiles(ffmpegRoot, "*.cs", SearchOption.AllDirectories)
                .SelectMany(file => File.ReadLines(file)
                    .Select((line, index) => (line, index))
                    .Where(entry => !entry.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && forbidden.IsMatch(entry.line))
                    .Select(entry => $"{Path.GetFileName(file)}:{entry.index + 1}"))
                .ToArray();

            Assert.True(offenders.Length == 0, $"Network API in ffprobe sources: {string.Join(", ", offenders)}");
        }

        [Fact]
        public async Task GetLicenseAsync_NamesTheSourceOfTheBinaryInUse()
        {
            var applicationBase = FileService.GetTempDirectory("ffprobe-license-app");
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-license-legacy");
            var service = CreateService(new Mock<IProcessRunner>().AnswerFfprobeVersionCheck().Object, legacyDirectory, applicationBase);

            Assert.Equal(string.Empty, await service.GetLicenseAsync());

            await File.WriteAllTextAsync(Path.Join(legacyDirectory, ExecutableName), "placed by hand");
            Assert.Equal(FfmpegService.LegacyLicenseNotice, await service.GetLicenseAsync());

            await File.WriteAllTextAsync(Path.Join(applicationBase, ExecutableName), "packaged");
            var notice = await service.GetLicenseAsync();
            Assert.Contains("Openur.FFprobeStatic", notice);
            Assert.NotEqual(FfmpegService.LegacyLicenseNotice, notice);
        }

        [Fact]
        public void PackagedLicenseNotice_NamesTheLicenceAndPointsAtTheShippedFiles()
        {
            var notice = FfmpegService.PackagedLicenseNotice("9.0.2-example");

            Assert.Contains("LGPL version 3 or later", notice);
            Assert.DoesNotContain("LGPL/GPL", notice);
            Assert.Contains("licenses/ffprobe/", notice);
            // The build copies licenses/ffprobe/ into every output, the test output included, so
            // the directory the notice names is really there next to the application.
            foreach (var file in new[] { "NOTICE", "LGPL-3.0.txt", "GPL-3.0.txt", "dav1d-BSD-2-Clause.txt" })
            {
                Assert.True(
                    File.Exists(Path.Join(AppContext.BaseDirectory, "licenses", "ffprobe", file)),
                    $"licenses/ffprobe/{file} is missing from the build output");
            }
        }

        [Fact]
        public async Task GetLicenseAsync_PackagedBinary_NamesTheVersionTheBinaryReports()
        {
            // The version comes from -version rather than a constant, so a package bump changes
            // the notice without a code change.
            var applicationBase = FileService.GetTempDirectory("ffprobe-license-version");
            await File.WriteAllTextAsync(Path.Join(applicationBase, ExecutableName), "packaged");
            var processRunner = new Mock<IProcessRunner>().AnswerFfprobeVersionCheck(
                new ProcessResult(0, "ffprobe version 10.1-example Copyright (c) 2007-2026 the FFmpeg developers\n", string.Empty, false));
            var service = CreateService(processRunner.Object, Path.Join(FileService.GetTempPath(), "ffmpeg-unused"), applicationBase);

            var notice = await service.GetLicenseAsync();

            Assert.StartsWith("ffprobe 10.1-example is a static FFmpeg build", notice);
            Assert.DoesNotContain("9.0.2", notice);
        }

        [Fact]
        public async Task GetLicenseAsync_PackagedBinaryFailingTheCheck_OmitsTheVersion()
        {
            var applicationBase = FileService.GetTempDirectory("ffprobe-license-failing");
            await File.WriteAllTextAsync(Path.Join(applicationBase, ExecutableName), "packaged");
            var processRunner = new Mock<IProcessRunner>().AnswerFfprobeVersionCheck(
                new ProcessResult(1, string.Empty, "not an ffprobe", false));
            var service = CreateService(processRunner.Object, Path.Join(FileService.GetTempPath(), "ffmpeg-unused"), applicationBase);

            Assert.StartsWith("ffprobe is a static FFmpeg build", await service.GetLicenseAsync());
        }

        [Fact]
        public async Task GetFfprobePathAsync_PackagedAndLegacyBothPresent_PrefersPackaged()
        {
            var applicationBase = FileService.GetTempDirectory("ffprobe-app");
            var packaged = Path.Join(applicationBase, ExecutableName);
            await File.WriteAllTextAsync(packaged, "packaged");
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy");
            await File.WriteAllTextAsync(Path.Join(legacyDirectory, ExecutableName), "downloaded by an older release");
            var service = CreateService(new Mock<IProcessRunner>().AnswerFfprobeVersionCheck().Object, legacyDirectory, applicationBase);

            Assert.Equal(packaged, await service.GetFfprobePathAsync());
        }

        [Fact]
        public async Task GetFfprobePathAsync_OnlyLegacyPresent_FallsBackToIt()
        {
            var applicationBase = FileService.GetTempDirectory("ffprobe-app-empty");
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-only");
            var legacy = Path.Join(legacyDirectory, ExecutableName);
            await File.WriteAllTextAsync(legacy, "placed by hand");
            var service = CreateService(new Mock<IProcessRunner>().AnswerFfprobeVersionCheck().Object, legacyDirectory, applicationBase);

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
                new Mock<IProcessRunner>(MockBehavior.Strict).AnswerFfprobeVersionCheck().Object,
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
            string? applicationBaseDirectory)
        {
            return new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                processRunner,
                Mock.Of<IApplicationPathService>(service => service.FfmpegRootPath == legacyDirectory),
                applicationBaseDirectory,
                RuntimeInformation.RuntimeIdentifier);
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
