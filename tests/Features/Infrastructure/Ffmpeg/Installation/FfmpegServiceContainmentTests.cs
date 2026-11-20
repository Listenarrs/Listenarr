using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// FfmpegService only chmods and runs an ffprobe that resolves inside the application
    /// directory or the configured ffmpeg directory. Each fake ffprobe is a shell script that
    /// records every run in a marker file, so a test can tell whether it was executed as well as
    /// whether its mode was changed.
    /// </summary>
    [Trait("Name", "FfmpegServiceContainmentTests")]
    [Trait("Category", "FfmpegService")]
    public class FfmpegServiceContainmentTests : BaseTests
    {
        private const UnixFileMode NotExecutable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        private const UnixFileMode Executable = NotExecutable | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        [LinuxFileLinkFact]
        [SupportedOSPlatform("linux")]
        public async Task Startup_LegacySymlinkToAFileOutsideTheRoots_IsNeitherChmoddedNorRun()
        {
            var external = await WriteFakeFfprobeAsync(FileService.GetTempDirectory("ffprobe-outside-startup"), NotExecutable);
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-startup");
            File.CreateSymbolicLink(Path.Join(legacyDirectory, "ffprobe"), external.Path);
            var service = CreateService(legacyDirectory, FileService.GetTempDirectory("ffprobe-app-startup"));

            Assert.Null(await service.EnsureFfprobeInstalledAsync());

            Assert.Equal(NotExecutable, File.GetUnixFileMode(external.Path));
            Assert.False(File.Exists(external.Marker), "the external ffprobe was run");
        }

        [LinuxFileLinkFact]
        [SupportedOSPlatform("linux")]
        public async Task Discovery_LegacySymlinkToAFileOutsideTheRoots_IsNotRun()
        {
            // Already executable, so only the containment check can keep discovery from running it.
            var external = await WriteFakeFfprobeAsync(FileService.GetTempDirectory("ffprobe-outside-discovery"), Executable);
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-discovery");
            File.CreateSymbolicLink(Path.Join(legacyDirectory, "ffprobe"), external.Path);
            var service = CreateService(legacyDirectory, FileService.GetTempDirectory("ffprobe-app-discovery"));

            Assert.Null(await service.GetFfprobePathAsync());

            Assert.False(File.Exists(external.Marker), "the external ffprobe was run");
        }

        [LinuxFileLinkFact]
        [SupportedOSPlatform("linux")]
        public async Task PackagedSymlinkToAFileOutsideTheRoots_IsRejectedWithoutFallingBackToTheLegacyCopy()
        {
            var external = await WriteFakeFfprobeAsync(FileService.GetTempDirectory("ffprobe-outside-packaged"), Executable);
            var applicationBase = FileService.GetTempDirectory("ffprobe-app-packaged-link");
            File.CreateSymbolicLink(Path.Join(applicationBase, "ffprobe"), external.Path);
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-behind-packaged-link");
            var legacy = await WriteFakeFfprobeAsync(legacyDirectory, Executable);
            var service = CreateService(legacyDirectory, applicationBase);

            Assert.Null(await service.GetFfprobePathAsync());
            Assert.Null(await service.EnsureFfprobeInstalledAsync());

            Assert.False(File.Exists(external.Marker), "the external ffprobe was run");
            Assert.False(File.Exists(legacy.Marker), "the legacy ffprobe was run after the packaged one was rejected");
        }

        [LinuxFact]
        [SupportedOSPlatform("linux")]
        public async Task LegacyBinaryInsideTheRoots_IsChmoddedRunAndUsed()
        {
            // Control for the tests above: the same fake ffprobe as a plain file in the ffmpeg
            // directory is made executable, run and used, so a missing marker or an unchanged
            // mode there means the binary was kept from running.
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-inside");
            var legacy = await WriteFakeFfprobeAsync(legacyDirectory, NotExecutable);
            var service = CreateService(legacyDirectory, FileService.GetTempDirectory("ffprobe-app-inside"));

            Assert.Equal(legacy.Path, await service.EnsureFfprobeInstalledAsync());
            Assert.Equal(legacy.Path, await service.GetFfprobePathAsync());

            Assert.Equal(Executable, File.GetUnixFileMode(legacy.Path));
            Assert.True(File.Exists(legacy.Marker), "the legacy ffprobe was not run");
        }

        [LinuxFileLinkFact]
        [SupportedOSPlatform("linux")]
        public async Task LegacySymlinkToAFileInsideTheLegacyDirectory_IsUsed()
        {
            // Containment is checked on where the link leads, so a link that stays inside the
            // ffmpeg directory is still accepted.
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-internal-link");
            var target = await WriteFakeFfprobeAsync(FileService.GetTempDirectory(Path.Join(legacyDirectory, "bin")), Executable);
            var link = Path.Join(legacyDirectory, "ffprobe");
            File.CreateSymbolicLink(link, target.Path);
            var service = CreateService(legacyDirectory, FileService.GetTempDirectory("ffprobe-app-internal-link"));

            Assert.Equal(link, await service.GetFfprobePathAsync());

            Assert.True(File.Exists(target.Marker), "the linked ffprobe was not run");
        }

        [LinuxFileLinkFact]
        [SupportedOSPlatform("linux")]
        public async Task PackagedBinary_StillWinsOverALegacySymlinkOutsideTheRoots()
        {
            var external = await WriteFakeFfprobeAsync(FileService.GetTempDirectory("ffprobe-outside-shadowed"), NotExecutable);
            var legacyDirectory = FileService.GetTempDirectory("ffprobe-legacy-shadowed");
            File.CreateSymbolicLink(Path.Join(legacyDirectory, "ffprobe"), external.Path);
            var applicationBase = FileService.GetTempDirectory("ffprobe-app-shadowing");
            var packaged = await WriteFakeFfprobeAsync(applicationBase, Executable);
            var service = CreateService(legacyDirectory, applicationBase);

            Assert.Equal(packaged.Path, await service.EnsureFfprobeInstalledAsync());

            Assert.True(File.Exists(packaged.Marker), "the packaged ffprobe was not run");
            Assert.Equal(NotExecutable, File.GetUnixFileMode(external.Path));
            Assert.False(File.Exists(external.Marker), "the external ffprobe was run");
        }

        /// <summary>
        /// Write a shell script named ffprobe into <paramref name="directory"/> that appends to a
        /// marker file beside it and answers <c>-version</c> like ffprobe.
        /// </summary>
        [SupportedOSPlatform("linux")]
        private static async Task<(string Path, string Marker)> WriteFakeFfprobeAsync(string directory, UnixFileMode mode)
        {
            var path = Path.Join(directory, "ffprobe");
            var marker = Path.Join(directory, "ran");
            await File.WriteAllTextAsync(path, $"#!/bin/sh\necho ran >> '{marker}'\necho 'ffprobe version 9.0.2-example'\n");
            File.SetUnixFileMode(path, mode);
            return (path, marker);
        }

        private FfmpegService CreateService(string legacyDirectory, string applicationBaseDirectory)
        {
            return new FfmpegService(
                new Mock<ILogger<FfmpegService>>().Object,
                new UnredactedProcessRunner(),
                Mock.Of<IApplicationPathService>(service => service.FfmpegRootPath == legacyDirectory),
                applicationBaseDirectory,
                RuntimeInformation.RuntimeIdentifier);
        }
    }
}
