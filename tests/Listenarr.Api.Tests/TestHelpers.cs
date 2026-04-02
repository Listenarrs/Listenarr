using Asp.Versioning.ApiExplorer;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Api.Tests
{
    internal static class TestHelpers
    {
        /// <summary>
        /// Resolves the versioned API base path (e.g. "/api/v1") from the test server's service provider.
        /// </summary>
        public static string ResolveApiBasePath(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider.GetService<IApiVersionDescriptionProvider>();
            var groupName = provider?.ApiVersionDescriptions.FirstOrDefault(d => !d.IsDeprecated)?.GroupName
                ?? provider?.ApiVersionDescriptions.FirstOrDefault()?.GroupName;

            return string.IsNullOrWhiteSpace(groupName) ? "/api/v1" : $"/api/{groupName}";
        }
    }

    /// <summary>
    /// Simple IStartupConfigService stub for integration tests.
    /// </summary>
    internal class TestStartupConfigService : IStartupConfigService
    {
        private readonly StartupConfig _cfg;
        public TestStartupConfigService(StartupConfig cfg) { _cfg = cfg; }
        public StartupConfig? GetConfig() => _cfg;
        public Task ReloadAsync() => Task.CompletedTask;
        public Task SaveAsync(StartupConfig config) => Task.CompletedTask;
    }
}
