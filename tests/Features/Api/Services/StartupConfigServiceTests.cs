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
using Microsoft.Extensions.Logging;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Features.Api.Services
{
    public class StartupConfigServiceTests
    {
        [Fact]
        public async Task SaveAsync_PreservesAuthenticationRequired()
        {
            // arrange - ensure no existing config on disk
            var baseDir = AppContext.BaseDirectory;
            var cfgDir = Path.Join(baseDir, "config");

            using var loggerFactory = new LoggerFactory();
            var logger = loggerFactory.CreateLogger<StartupConfigService>();
            var envMock = new Moq.Mock<Microsoft.Extensions.Hosting.IHostEnvironment>();
            envMock.Setup(e => e.ContentRootPath).Returns(AppContext.BaseDirectory);

            try
            {
                if (Directory.Exists(cfgDir))
                {
                    Directory.Delete(cfgDir, recursive: true);
                }

                var svc = new StartupConfigService(logger, envMock.Object);

                // default config should exist and have false auth
                var original = svc.GetConfig();
                Assert.NotNull(original);
                Assert.Equal("false", original.AuthenticationRequired);

                // update port and also explicitly enable auth
                var modified = new StartupConfig
                {
                    AuthenticationRequired = "true",
                    Port = 12345,
                    ApiKey = original.ApiKey
                };

                await svc.SaveAsync(modified);

                // after saving, service config should reflect the new auth value and port
                var after = svc.GetConfig();
                Assert.NotNull(after);
                Assert.Equal("true", after.AuthenticationRequired);
                Assert.Equal(12345, after.Port);

                // the file on disk should also contain the updated auth flag
                var jsonPath = Path.Join(cfgDir, "config.json");
                Assert.True(File.Exists(jsonPath));
                var json = File.ReadAllText(jsonPath);
                Assert.Contains("\"AuthenticationRequired\": \"true\"", json);

                // now toggle back to false and save again
                var toggle = new StartupConfig
                {
                    AuthenticationRequired = "false",
                    Port = 54321,
                    ApiKey = after.ApiKey
                };

                await svc.SaveAsync(toggle);
                var after2 = svc.GetConfig();
                Assert.Equal("false", after2.AuthenticationRequired);
                Assert.Equal(54321, after2.Port);
                json = File.ReadAllText(jsonPath);
                Assert.Contains("\"AuthenticationRequired\": \"false\"", json);
            }
            finally
            {
                if (Directory.Exists(cfgDir))
                    Directory.Delete(cfgDir, recursive: true);
            }
        }
    }
}
