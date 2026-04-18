using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Utils;
using Listenarr.Api.Services.Adapters;

namespace Listenarr.Api.Tests
{
    public class ConfigurationServiceTests
    {
        [Fact]
        public async Task SaveApplicationSettings_PersistsChanges()
        {
            var testOutputPath = FileUtils.GetAbsolutePath("test-output");
            var partialUpdatePath = FileUtils.GetAbsolutePath("partial-update");

            // Arrange - build service provider with in-memory DB
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ListenArrDbContext>(opts => opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));

            var provider = services.BuildServiceProvider(validateScopes: true);

            // Resolve scoped DB context from a scope (AddDbContext registers it as scoped)
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<ConfigurationService>>();

            var mockUser = new Mock<IUserService>();
            var mockStartup = new Mock<IStartupConfigService>();

            var svc = new ConfigurationService(db, logger, mockUser.Object, mockStartup.Object, new Mock<IDownloadClientAdapterFactory>().Object);

            // Act - save a modified settings object
            var settings = await svc.GetApplicationSettingsAsync();
            settings.OutputPath = testOutputPath;
            settings.ShowCompletedExternalDownloads = true;
            settings.EnabledNotificationTriggers = new System.Collections.Generic.List<string> { "book-added", "book-completed" };
            settings.Webhooks = new System.Collections.Generic.List<WebhookConfiguration>
            {
                new WebhookConfiguration { Name = "UnitWebhook", Url = "https://example.test/webhook", Type = "Zapier" }
            };

            await svc.SaveApplicationSettingsAsync(settings);

            // Read back
            var saved = await svc.GetApplicationSettingsAsync();

            // Assert
            Assert.Equal(testOutputPath, saved.OutputPath);
            Assert.True(saved.ShowCompletedExternalDownloads);
            Assert.NotNull(saved.EnabledNotificationTriggers);
            Assert.Contains("book-completed", saved.EnabledNotificationTriggers);
            Assert.NotNull(saved.Webhooks);
            Assert.Single(saved.Webhooks!);
            Assert.Equal("UnitWebhook", saved.Webhooks![0].Name);

            // Now simulate a partial update where collections are omitted from the payload
            var partial = new ApplicationSettings { Id = 1, OutputPath = partialUpdatePath };
            await svc.SaveApplicationSettingsAsync(partial);

            var afterPartial = await svc.GetApplicationSettingsAsync();
            // Ensure previously saved collections were not cleared by the partial update
            Assert.Equal(partialUpdatePath, afterPartial.OutputPath);
            Assert.NotNull(afterPartial.EnabledNotificationTriggers);
            Assert.Contains("book-completed", afterPartial.EnabledNotificationTriggers);
            Assert.NotNull(afterPartial.Webhooks);
            Assert.Single(afterPartial.Webhooks!);
            Assert.Equal("UnitWebhook", afterPartial.Webhooks![0].Name);
        }

        [Fact]
        public async Task InMemoryDb_Persists_Webhooks_Directly()
        {
            // Arrange - build service provider with in-memory DB
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ListenArrDbContext>(opts => opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));

            var provider = services.BuildServiceProvider(validateScopes: true);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();

            // Act - ensure settings exist, set webhooks directly, save, read back
            var settings = await db.ApplicationSettings.FirstOrDefaultAsync(s => s.Id == 1);
            if (settings == null)
            {
                settings = new ApplicationSettings();
                db.ApplicationSettings.Add(settings);
                await db.SaveChangesAsync();
            }

            settings.Webhooks = new System.Collections.Generic.List<WebhookConfiguration>
            {
                new WebhookConfiguration { Name = "DirectWebhook", Url = "https://example.test/direct", Type = "Zapier" }
            };

            await db.SaveChangesAsync();

            var reloaded = await db.ApplicationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);

            // Assert
            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded!.Webhooks);
            Assert.Single(reloaded.Webhooks!);
            Assert.Equal("DirectWebhook", reloaded.Webhooks![0].Name);
        }

        [Fact]
        public async Task ProwlarrImportSettings_ApiKey_IsEncryptedAtRest_AndRecoveredForServerUse()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ListenArrDbContext>(opts => opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));

            var provider = services.BuildServiceProvider(validateScopes: true);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<ConfigurationService>>();

            var mockUser = new Mock<IUserService>();
            var mockStartup = new Mock<IStartupConfigService>();

            var svc = new ConfigurationService(db, logger, mockUser.Object, mockStartup.Object, new Mock<IDownloadClientAdapterFactory>().Object);

            await svc.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "super-secret-prowlarr-key",
                TagFilter = "audiobooks",
            });

            var stored = await db.ApplicationSettings.AsNoTracking().FirstAsync(s => s.Id == 1);
            Assert.Equal("http://localhost", stored.ProwlarrUrl);
            Assert.Equal(9696, stored.ProwlarrPort);
            Assert.Equal("audiobooks", stored.ProwlarrTagFilter);
            Assert.False(string.IsNullOrWhiteSpace(stored.ProwlarrApiKeyEncrypted));
            Assert.NotEqual("super-secret-prowlarr-key", stored.ProwlarrApiKeyEncrypted);

            var frontendView = await svc.GetProwlarrImportSettingsAsync(includeSecret: false);
            Assert.True(frontendView.HasSavedApiKey);
            Assert.Null(frontendView.ApiKey);
            Assert.Equal("audiobooks", frontendView.TagFilter);

            var serverView = await svc.GetProwlarrImportSettingsAsync(includeSecret: true);
            Assert.True(serverView.HasSavedApiKey);
            Assert.Equal("super-secret-prowlarr-key", serverView.ApiKey);
            Assert.Equal("audiobooks", serverView.TagFilter);
        }

        [Fact]
        public async Task SaveApplicationSettings_PreservesSavedProwlarrImportSettings_WhenPayloadOmitsThem()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ListenArrDbContext>(opts => opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));

            var provider = services.BuildServiceProvider(validateScopes: true);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<ConfigurationService>>();

            var svc = new ConfigurationService(
                db,
                logger,
                new Mock<IUserService>().Object,
                new Mock<IStartupConfigService>().Object,
                new Mock<IDownloadClientAdapterFactory>().Object);

            await svc.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "saved-secret",
                TagFilter = "audiobooks"
            });

            await svc.SaveApplicationSettingsAsync(new ApplicationSettings
            {
                Id = 1,
                OutputPath = FileUtils.GetAbsolutePath("updated-output")
            });

            var savedConnection = await svc.GetProwlarrImportSettingsAsync(includeSecret: true);
            Assert.Equal("http://localhost", savedConnection.Url);
            Assert.Equal(9696, savedConnection.Port);
            Assert.Equal("saved-secret", savedConnection.ApiKey);
            Assert.Equal("audiobooks", savedConnection.TagFilter);

            var stored = await db.ApplicationSettings.AsNoTracking().FirstAsync(s => s.Id == 1);
            Assert.False(string.IsNullOrWhiteSpace(stored.ProwlarrApiKeyEncrypted));
        }

    }
}
