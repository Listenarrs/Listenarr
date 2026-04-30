using Listenarr.Api.Services;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Mocks
{
    /// <summary>
    /// Simple IStartupConfigService stub for integration tests.
    /// </summary>
    internal class StartupConfigServiceMock : IStartupConfigService
    {
        private readonly StartupConfig _cfg;
        public StartupConfigServiceMock(StartupConfig cfg) { _cfg = cfg; }
        public StartupConfig? GetConfig() => _cfg;
        public Task ReloadAsync() => Task.CompletedTask;
        public Task SaveAsync(StartupConfig config) => Task.CompletedTask;
    }
}
