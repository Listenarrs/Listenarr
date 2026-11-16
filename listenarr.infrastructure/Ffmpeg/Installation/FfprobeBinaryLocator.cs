/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using System.Runtime.InteropServices;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// Finds the ffprobe binary restored from the Openur.FFprobeStatic NuGet package, the same
    /// package Sonarr ships. The package carries one native asset per portable RID under
    /// <c>runtimes/&lt;rid&gt;/native/</c>. A RID-specific publish (every release artifact and
    /// the CI Docker images) flattens the current RID's asset next to the application; a
    /// RID-agnostic build (<c>dotnet run</c>, the tests, the root Dockerfile) keeps the
    /// <c>runtimes</c> tree. Both layouts are checked, and only for the given RIDs, so a binary
    /// built for another platform is never picked up.
    /// </summary>
    internal static class FfprobeBinaryLocator
    {
        public static string ExecutableName(bool isWindows) => isWindows ? "ffprobe.exe" : "ffprobe";

        public static IReadOnlyList<string> CandidatePaths(string applicationBaseDirectory, string runtimeIdentifier, bool isWindows)
        {
            return CandidatePaths(applicationBaseDirectory, [runtimeIdentifier], isWindows);
        }

        public static IReadOnlyList<string> CandidatePaths(string applicationBaseDirectory, IReadOnlyList<string> runtimeIdentifiers, bool isWindows)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
            ArgumentNullException.ThrowIfNull(runtimeIdentifiers);

            var executable = ExecutableName(isWindows);
            var candidates = new List<string> { Path.Join(applicationBaseDirectory, executable) };
            foreach (var runtimeIdentifier in runtimeIdentifiers.Where(rid => !string.IsNullOrWhiteSpace(rid)).Distinct(StringComparer.Ordinal))
            {
                candidates.Add(Path.Join(applicationBaseDirectory, "runtimes", runtimeIdentifier, "native", executable));
            }

            return candidates;
        }

        public static string? Locate(string applicationBaseDirectory, string runtimeIdentifier, bool isWindows)
        {
            return Locate(applicationBaseDirectory, [runtimeIdentifier], isWindows);
        }

        public static string? Locate(string applicationBaseDirectory, IReadOnlyList<string> runtimeIdentifiers, bool isWindows)
        {
            return CandidatePaths(applicationBaseDirectory, runtimeIdentifiers, isWindows).FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// The RIDs to look under for this process: the runtime's own RID first, then the portable
        /// ones. A distro-built .NET reports a distro RID such as <c>ubuntu.24.04-x64</c> or
        /// <c>alpine.3.23-x64</c>, which the package does not carry.
        /// </summary>
        public static IReadOnlyList<string> CurrentRuntimeIdentifiers()
        {
            var portable = PortableRuntimeIdentifiers(
                CurrentOperatingSystemMoniker(),
                RuntimeInformation.ProcessArchitecture,
                OperatingSystem.IsLinux() && IsMuslLinux());
            return new[] { RuntimeInformation.RuntimeIdentifier }.Concat(portable).Distinct(StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// Portable RIDs for a platform. On musl Linux the musl build comes first, with the glibc
        /// one after it, which is statically linked and also runs there.
        /// </summary>
        public static IReadOnlyList<string> PortableRuntimeIdentifiers(string? operatingSystem, Architecture architecture, bool isMusl)
        {
            var arch = architecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                Architecture.X86 => "x86",
                _ => null
            };
            if (operatingSystem == null || arch == null)
            {
                return [];
            }

            return isMusl && operatingSystem == "linux"
                ? [$"linux-musl-{arch}", $"linux-{arch}"]
                : [$"{operatingSystem}-{arch}"];
        }

        private static string? CurrentOperatingSystemMoniker()
        {
            if (OperatingSystem.IsWindows()) return "win";
            if (OperatingSystem.IsMacOS()) return "osx";
            if (OperatingSystem.IsLinux()) return "linux";
            if (OperatingSystem.IsFreeBSD()) return "freebsd";
            return null;
        }

        private static bool IsMuslLinux()
        {
            try
            {
                return Directory.EnumerateFiles("/lib", "ld-musl-*").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
