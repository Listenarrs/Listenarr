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
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.SystemDiagnostics.Processes
{
    public class SystemProcessRunnerTests
    {
        [Fact]
        public async Task RunAsync_RedactsTransientSensitiveValues()
        {
            var logger = new NullLogger<SystemProcessRunner>();
            var runner = new SystemProcessRunner(logger);

            var secret = "TRANSIENT-SECRET-123";

            var psi = CreateEchoProcessStartInfo(secret);

            using var reg = runner.RegisterTransientSensitive(new[] { secret });
            var result = await runner.RunAsync(psi, 5000);

            Assert.DoesNotContain(secret, result.Stdout);
            Assert.Contains("<redacted>", result.Stdout);
        }

        [Fact]
        public async Task RunAsync_DoesNotRedact_WhenNotRegistered()
        {
            var logger = new NullLogger<SystemProcessRunner>();
            var runner = new SystemProcessRunner(logger);

            var secret = "TRANSIENT-SECRET-456";
            var psi = CreateEchoProcessStartInfo(secret);

            var result = await runner.RunAsync(psi, 5000);

            Assert.Contains(secret, result.Stdout);
        }

        private static ProcessStartInfo CreateEchoProcessStartInfo(string text)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/s");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add($"echo {text}");
                return startInfo;
            }

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("printf '%s\\n' \"$1\"");
            startInfo.ArgumentList.Add("sh");
            startInfo.ArgumentList.Add(text);
            return startInfo;
        }
    }
}
