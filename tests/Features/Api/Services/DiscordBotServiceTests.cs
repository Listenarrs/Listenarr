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

namespace Listenarr.Tests.Features.Api.Services
{
    public class DiscordBotServiceTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _output;

        public DiscordBotServiceTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _output = output;
        }
        // A simple fake StartupConfigService for tests
        private class FakeStartupConfigService : IStartupConfigService
        {
            private readonly StartupConfig _cfg;
            public FakeStartupConfigService(StartupConfig cfg) => _cfg = cfg;
            public StartupConfig? GetConfig() => _cfg;
            public bool IsAuthenticationRequired() => _cfg.IsAuthenticationEnabled();
            public string GetEffectiveApiVersion(string? requestedApiVersion = null) => NormalizeApiVersion(_cfg.ApiVersion, requestedApiVersion);
            public string NormalizeApiVersion(string? configuredApiVersion, string? requestedApiVersion = null)
                => ApiVersionNormalizer.NormalizeApiVersionString(configuredApiVersion)
                   ?? ApiVersionNormalizer.NormalizeApiVersionString(requestedApiVersion)
                   ?? ApiVersionNormalizer.DefaultApiVersion;
            public Task ReloadAsync() => Task.CompletedTask;
            public Task SaveAsync(StartupConfig config) { return Task.CompletedTask; }
        }

        // Fake IProcessRunner that returns a controllable long-running process
        private class FakeProcessRunner : IProcessRunner
        {
            public ProcessStartInfo? LastStartedProcessStartInfo { get; private set; }

            public Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMs = 60000, CancellationToken cancellationToken = default)
            {
                // Simulate node --version preflight success
                if (startInfo.FileName == "node" || (startInfo.FileName != null && startInfo.FileName.EndsWith("node", StringComparison.OrdinalIgnoreCase)))
                {
                    return Task.FromResult(new ProcessResult(0, "v-test", string.Empty, false));
                }

                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false));
            }

            public Process StartProcess(ProcessStartInfo startInfo)
            {
                LastStartedProcessStartInfo = startInfo;

                // Start a short-lived sleeper process so the service sees a running Process
                var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c ping -n 30 127.0.0.1 > nul", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }
                    : new ProcessStartInfo { FileName = "sleep", Arguments = "30", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };

                var proc = new Process { StartInfo = psi };
                proc.Start();
                return proc;
            }

            public System.IDisposable RegisterTransientSensitive(System.Collections.Generic.IEnumerable<string> values)
            {
                // Tests don't need transient redaction behavior; return a no-op disposable
                return new NoopDisposable();
            }

            private class NoopDisposable : IDisposable { public void Dispose() { } }
        }

        [Fact]
        public async Task StartAndStopBot_WithFakeRunner_StartsAndStopsProcess()
        {
            // Arrange
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr_test_discord_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var botDir = Path.Join(tempRoot, "tools", "discord-bot");
            Directory.CreateDirectory(botDir);
            // Create a dummy index.js so DiscordBotService can find it
            File.WriteAllText(Path.Join(botDir, "index.js"), "console.log('dummy'); setTimeout(()=>{}, 100000);");

            var pathService = new Mock<IApplicationPathService>();
            pathService.SetupGet(service => service.ContentRootPath).Returns(tempRoot);
            pathService.SetupGet(service => service.DiscordBotRootPath).Returns(botDir);
            var cfg = new StartupConfig { ApiKey = "test-api-key", EnableSsl = false, Port = 5000 };
            var startupService = new FakeStartupConfigService(cfg);
            var requestContextAccessor = Mock.Of<IRequestContextAccessor>();
            var logger = new Mock<ILogger<DiscordBotService>>().Object;
            var fakeRunner = new FakeProcessRunner();

            var svc = new DiscordBotService(logger, startupService, pathService.Object, requestContextAccessor, fakeRunner);

            try
            {
                // Debug: ensure test setup is correct
                _output.WriteLine($"[Test] ContentRootPath: {tempRoot}");
                _output.WriteLine($"[Test] Bot dir exists: {Directory.Exists(botDir)}");
                _output.WriteLine($"[Test] index.js exists: {File.Exists(Path.Join(botDir, "index.js"))}");

                // Act - start the bot
                var started = await svc.StartBotAsync();

                // Assert start was successful and bot is running
                Assert.True(started, "StartBotAsync should return true");
                var isRunning = await svc.IsBotRunningAsync();
                Assert.True(isRunning, "Bot should be running after StartBotAsync");
                Assert.Equal(botDir, fakeRunner.LastStartedProcessStartInfo?.WorkingDirectory);

                var status = await svc.GetBotStatusAsync();
                Assert.NotNull(status);
                Assert.Contains("running", status);

                // Act - stop the bot
                var stopped = await svc.StopBotAsync();
                Assert.True(stopped, "StopBotAsync should return true");

                var isRunningAfter = await svc.IsBotRunningAsync();
                Assert.False(isRunningAfter, "Bot should not be running after StopBotAsync");
            }
            finally
            {
                // Cleanup
                try { Directory.Delete(tempRoot, true); } catch (IOException ex) { _ = ex; } catch (UnauthorizedAccessException ex) { _ = ex; }
            }
        }
    }
}
