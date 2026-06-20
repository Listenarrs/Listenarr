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

namespace Listenarr.Infrastructure.SystemDiagnostics.Processes
{
    internal static class ProcessHelpers
    {
        public static string? FindExecutableOnPath(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var paths = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var exts = Environment.GetEnvironmentVariable("PATHEXT")?.Split(';') ?? new[] { ".EXE", ".CMD", ".BAT", ".PS1" };

            foreach (var dir in paths)
            {
                try
                {
                    var found = exts.Select(ext => Path.Join(dir, name + ext)).FirstOrDefault(File.Exists);
                    if (found != null) return found;

                    var candidateNoExt = Path.Join(dir, name);
                    if (File.Exists(candidateNoExt)) return candidateNoExt;
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            // Also try invoking default shell utilities on Unix-like systems (sh which)
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var which = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = name,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (which != null)
                    {
                        var outp = which.StandardOutput.ReadLine();
                        if (!string.IsNullOrEmpty(outp) && File.Exists(outp)) return outp;
                    }
                }
                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            return null;
        }
    }
}
