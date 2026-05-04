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
using System.Runtime.InteropServices;
using Listenarr.Infrastructure.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c echo {text}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
            }

            return new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"echo {text}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }
    }
}
