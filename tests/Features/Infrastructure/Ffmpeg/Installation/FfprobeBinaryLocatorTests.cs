using System.Xml.Linq;
using Arch = System.Runtime.InteropServices.Architecture;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// ffprobe arrives as a NuGet package (native asset per RID) in this option, so these tests
    /// pin down where the locator looks and that the build output really carries a binary for
    /// every RID Listenarr publishes.
    /// </summary>
    [Trait("Name", "FfprobeBinaryLocatorTests")]
    [Trait("Category", "FfmpegService")]
    public class FfprobeBinaryLocatorTests : BaseTests
    {
        [Fact]
        public async Task Locate_RidSpecificPublishLayout_ReturnsBinaryBesideApplication()
        {
            var appBase = FileService.GetTempDirectory("ffprobe-flat");
            var expected = Path.Join(appBase, "ffprobe");
            await File.WriteAllTextAsync(expected, "packaged");

            var located = FfprobeBinaryLocator.Locate(appBase, "linux-x64", isWindows: false);

            Assert.Equal(expected, located);
        }

        [Fact]
        public async Task Locate_RidAgnosticBuildLayout_ReturnsNativeAssetForThatRid()
        {
            var appBase = FileService.GetTempDirectory("ffprobe-runtimes");
            var expected = Path.Join(appBase, "runtimes", "linux-arm64", "native", "ffprobe");
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            await File.WriteAllTextAsync(expected, "packaged");

            var located = FfprobeBinaryLocator.Locate(appBase, "linux-arm64", isWindows: false);

            Assert.Equal(expected, located);
        }

        [Fact]
        public async Task Locate_OnlyAnotherRidPackaged_ReturnsNullRatherThanAForeignBinary()
        {
            var appBase = FileService.GetTempDirectory("ffprobe-foreign");
            var foreign = Path.Join(appBase, "runtimes", "osx-x64", "native", "ffprobe");
            Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
            await File.WriteAllTextAsync(foreign, "packaged");

            var located = FfprobeBinaryLocator.Locate(appBase, "linux-x64", isWindows: false);

            Assert.Null(located);
        }

        [Fact]
        public async Task Locate_Windows_LooksForTheExeName()
        {
            var appBase = FileService.GetTempDirectory("ffprobe-windows");
            var extensionless = Path.Join(appBase, "runtimes", "win-x64", "native", "ffprobe");
            Directory.CreateDirectory(Path.GetDirectoryName(extensionless)!);
            await File.WriteAllTextAsync(extensionless, "wrong name");

            Assert.Null(FfprobeBinaryLocator.Locate(appBase, "win-x64", isWindows: true));

            var expected = extensionless + ".exe";
            await File.WriteAllTextAsync(expected, "packaged");

            Assert.Equal(expected, FfprobeBinaryLocator.Locate(appBase, "win-x64", isWindows: true));
        }

        [Fact]
        public async Task Locate_DistroSpecificRid_FallsBackToPortableRid()
        {
            // Distro-built .NET reports e.g. "ubuntu.24.04-x64"; the package only has portable RIDs.
            var appBase = FileService.GetTempDirectory("ffprobe-distro");
            var expected = Path.Join(appBase, "runtimes", "linux-x64", "native", "ffprobe");
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            await File.WriteAllTextAsync(expected, "packaged");

            Assert.Null(FfprobeBinaryLocator.Locate(appBase, ["ubuntu.24.04-x64"], isWindows: false));
            Assert.Equal(
                expected,
                FfprobeBinaryLocator.Locate(appBase, ["ubuntu.24.04-x64", "linux-x64"], isWindows: false));
        }

        [Theory]
        [InlineData("linux", Arch.X64, false, new[] { "linux-x64" })]
        [InlineData("linux", Arch.Arm64, true, new[] { "linux-musl-arm64", "linux-arm64" })]
        [InlineData("linux", Arch.Arm, false, new[] { "linux-arm" })]
        [InlineData("osx", Arch.Arm64, false, new[] { "osx-arm64" })]
        [InlineData("win", Arch.X86, false, new[] { "win-x86" })]
        [InlineData("freebsd", Arch.X64, false, new[] { "freebsd-x64" })]
        public void PortableRuntimeIdentifiers_MapsPlatformAndArchitecture(
            string os,
            Arch architecture,
            bool isMusl,
            string[] expected)
        {
            Assert.Equal(expected, FfprobeBinaryLocator.PortableRuntimeIdentifiers(os, architecture, isMusl));
        }

        [Fact]
        public void PortableRuntimeIdentifiers_UnknownArchitecture_ReturnsNothing()
        {
            Assert.Empty(FfprobeBinaryLocator.PortableRuntimeIdentifiers("linux", Arch.S390x, isMusl: false));
        }

        [Fact]
        public void BuildOutput_CarriesPackagedFfprobeForEveryPublishedRid()
        {
            // The RIDs Listenarr publishes come from the API project itself, so adding a RID
            // there without a packaged ffprobe for it fails here instead of in a user's install.
            var apiProject = XDocument.Load(Path.Join(RepositoryRoot(), "listenarr.api", "Listenarr.Api.csproj"));
            var publishedRids = apiProject.Descendants("RuntimeIdentifiers")
                .Single()
                .Value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.NotEmpty(publishedRids);

            // The runtimes/<rid>/native path specifically: the flat candidate would let one
            // binary satisfy every RID if this assembly were ever built RID-specific.
            var missing = publishedRids
                .Where(rid => !File.Exists(FfprobeBinaryLocator.CandidatePaths(
                    AppContext.BaseDirectory,
                    rid,
                    isWindows: rid.StartsWith("win-", StringComparison.Ordinal))[1]))
                .ToArray();

            Assert.True(
                missing.Length == 0,
                $"No packaged ffprobe in the build output for: {string.Join(", ", missing)}");
        }

        [Fact]
        public void CurrentRuntime_HasALocatablePackagedBinary()
        {
            var located = FfprobeBinaryLocator.Locate(
                AppContext.BaseDirectory,
                FfprobeBinaryLocator.CurrentRuntimeIdentifiers(),
                OperatingSystem.IsWindows());

            Assert.NotNull(located);
            Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(located));
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
    }
}
