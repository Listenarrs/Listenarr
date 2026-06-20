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

namespace Listenarr.Application.SystemDiagnostics.Contracts
{
    public record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    public interface IProcessRunner
    {
        Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = 60000, CancellationToken cancellationToken = default);
        // Start a long-running process and return the Process instance so callers can interact with it (kill, read streams, etc.).
        // Implementations should not swallow exceptions - callers rely on the returned Process instance.
        Process StartProcess(ProcessStartInfo startInfo);
        // Register transient sensitive values (e.g. API keys passed at runtime) which should be
        // redacted from process outputs. Returns an IDisposable that removes the values when disposed.
        IDisposable RegisterTransientSensitive(IEnumerable<string> values);
    }
}
