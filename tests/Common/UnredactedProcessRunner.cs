using System.Diagnostics;

namespace Listenarr.Tests.Common;

/// <summary>
/// Starts the process for real without SystemProcessRunner's output redaction. That runner
/// regex-redacts every process's stdout against process-wide secret values, and under the full
/// parallel suite another test's values corrupted ffprobe's JSON. Tests that run the packaged
/// ffprobe are about the binary, so they use this one.
/// </summary>
internal sealed class UnredactedProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = 60000, CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Stop the child, as SystemProcessRunner does, instead of leaving it running after
            // the test has moved on.
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(-1, string.Empty, string.Empty, true);
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr, false);
    }

    public Process StartProcess(ProcessStartInfo startInfo)
    {
        throw new NotSupportedException();
    }

    public IDisposable RegisterTransientSensitive(IEnumerable<string> values)
    {
        throw new NotSupportedException();
    }
}
