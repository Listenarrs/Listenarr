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
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.SystemDiagnostics.Processes
{
    public class SystemProcessRunner : IProcessRunner
    {
        private readonly ILogger<SystemProcessRunner> _logger;
        private readonly ConcurrentDictionary<string, int> _transientSensitiveCounts = new(StringComparer.Ordinal);

        public SystemProcessRunner(ILogger<SystemProcessRunner> logger)
        {
            _logger = logger;
        }

        public async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = 60000, CancellationToken cancellationToken = default)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            using var process = new Process();
            process.StartInfo = startInfo;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;

            process.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var exited = await Task.Run(() => process.WaitForExit(timeoutMs), cancellationToken);
                if (!exited)
                {
                    try { process.Kill(true); }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to kill timed-out process {FileName} {Args}", startInfo.FileName, startInfo.Arguments);
                    }
                    _logger.LogWarning("Process timed out after {Timeout}ms: {FileName} {Args}", timeoutMs, startInfo.FileName, startInfo.Arguments);
                    return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), true);
                }

                // Ensure asynchronous output readers have flushed into our buffers before reading them.
                try { process.WaitForExit(); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed waiting for process output readers to flush for {FileName} {Args}", startInfo.FileName, startInfo.Arguments);
                }

                var exit = process.ExitCode;

                // Collect sensitive values: environment + any transient values registered by callers
                var sensitiveFromEnv = LogRedaction.GetSensitiveValuesFromEnvironment();
                var transient = _transientSensitiveCounts.Keys;
                var combined = sensitiveFromEnv.Concat(transient).Where(v => !string.IsNullOrEmpty(v));

                var outText = LogRedaction.RedactText(stdout.ToString(), combined);
                var errText = LogRedaction.RedactText(stderr.ToString(), combined);

                return new ProcessResult(exit, outText, errText, false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Process run cancelled for {File} {Args}", startInfo.FileName, startInfo.Arguments);
                return new ProcessResult(-1, stdout.ToString(), stderr + "\nOperationCanceled", false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Include exception details in the returned stderr so callers can surface meaningful diagnostics
                var errText = stderr.ToString();
                try
                {
                    errText = string.IsNullOrEmpty(errText) ? ex.ToString() : errText + "\n" + ex;
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { errText = ex.Message; }

                // Redact sensitive values from the collected output before returning
                var sensitiveFromEnvEx = LogRedaction.GetSensitiveValuesFromEnvironment();
                var transientEx = _transientSensitiveCounts.Keys;
                var combinedEx = sensitiveFromEnvEx.Concat(transientEx).Where(v => !string.IsNullOrEmpty(v));
                var outTextEx = LogRedaction.RedactText(stdout.ToString(), combinedEx);
                var errTextEx = LogRedaction.RedactText(errText, combinedEx);

                _logger.LogWarning(ex, "Process runner threw an exception for {File} {Args}", startInfo.FileName, startInfo.Arguments);
                return new ProcessResult(-1, outTextEx, errTextEx, false);
            }
        }

        public Process StartProcess(ProcessStartInfo startInfo)
        {
            try
            {
                var process = new Process();
                process.StartInfo = startInfo;
                // Do not override startInfo properties like RedirectStandardOutput/Error or UseShellExecute - caller configures them.
                process.Start();
                return process;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to start process {File} {Args}", startInfo.FileName, startInfo.Arguments);
                throw;
            }
        }

        public IDisposable RegisterTransientSensitive(IEnumerable<string> values)
        {
            if (values == null) return new DisposableAction(() => { });

            var added = new List<string>();
            foreach (var v in values.Where(x => !string.IsNullOrEmpty(x)))
            {
                _transientSensitiveCounts.AddOrUpdate(v!, 1, (_, old) => old + 1);
                added.Add(v!);
            }

            return new DisposableAction(() =>
            {
                foreach (var v in added)
                {
                    _transientSensitiveCounts.AddOrUpdate(v, 0, (_, old) => Math.Max(0, old - 1));
                    if (_transientSensitiveCounts.TryGetValue(v, out var cnt) && cnt == 0)
                    {
                        _transientSensitiveCounts.TryRemove(v, out _);
                    }
                }
            });
        }

        private class DisposableAction : IDisposable
        {
            private readonly Action _action;
            private bool _disposed;
            public DisposableAction(Action action) => _action = action ?? (() => { });
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try { _action(); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    Debug.WriteLine($"SystemProcessRunner.DisposableAction cleanup failed: {ex.Message}");
                }
            }
        }
    }
}

