using System.Runtime.InteropServices;

namespace Listenarr.Api.Services
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
                    foreach (var ext in exts)
                    {
                        var candidate = Path.Combine(dir, name + ext);
                        if (File.Exists(candidate)) return candidate;
                    }

                    var candidateNoExt = Path.Combine(dir, name);
                    if (File.Exists(candidateNoExt)) return candidateNoExt;
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
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
                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { 
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            return null;
        }
    }
} 