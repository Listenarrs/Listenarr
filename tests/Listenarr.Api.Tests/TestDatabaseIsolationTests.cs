using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class TestDatabaseIsolationTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public TestDatabaseIsolationTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public void TestHost_UsesIsolatedSqlitePath_NotRepoConfigDatabase()
        {
            using var scope = _factory.Services.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

            var configuredPath = config["Listenarr:SqliteDbPath"];
            Assert.False(string.IsNullOrWhiteSpace(configuredPath));

            var resolvedPath = Path.GetFullPath(
                Path.IsPathRooted(configuredPath!)
                    ? configuredPath!
                    : Path.Combine(env.ContentRootPath, configuredPath!));

            var repoDbPath = Path.GetFullPath(Path.Combine(env.ContentRootPath, "config", "database", "listenarr.db"));
            Assert.False(
                string.Equals(resolvedPath, repoDbPath, StringComparison.OrdinalIgnoreCase),
                $"Test host resolved sqlite path to repo DB: {resolvedPath}");

            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            Assert.True(
                resolvedPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase),
                $"Expected test sqlite path under temp root '{tempRoot}', got '{resolvedPath}'.");
            Assert.Contains("listenarr-tests", resolvedPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}

