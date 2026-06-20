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
using System.Diagnostics;

namespace Listenarr.Infrastructure.Persistence
{
    public interface IProcessExecutionStore
    {
        Task SaveAsync(ProcessResult result, string? source = null, ProcessStartInfo? startInfo = null, CancellationToken cancellationToken = default);
    }

    public class ProcessExecutionStore : IProcessExecutionStore
    {
        private readonly IProcessExecutionLogRepository _logs;

        public ProcessExecutionStore(IProcessExecutionLogRepository logs)
        {
            _logs = logs;
        }

        public async Task SaveAsync(ProcessResult result, string? source = null, ProcessStartInfo? startInfo = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var entity = new ProcessExecutionLog
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Source = source,
                    FileName = startInfo?.FileName,
                    Arguments = startInfo?.Arguments,
                    ExitCode = result.ExitCode,
                    TimedOut = result.TimedOut,
                    Stdout = result.Stdout,
                    Stderr = result.Stderr,
                    DurationMs = null
                };

                await _logs.AddAsync(entity, cancellationToken);
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                // Swallow errors here - persistence is best-effort to avoid disrupting process flows.
                Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
        }
    }
}
