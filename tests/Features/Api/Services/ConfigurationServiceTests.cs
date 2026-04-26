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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;
using Listenarr.Domain.Utils;

namespace Listenarr.Tests.Features.Api.Services
{
    public class ConfigurationServiceTests
    {
        private static ConfigurationService BuildService(ListenArrDbContext db, ILogger<ConfigurationService> logger,
            IUserService? userService = null, IStartupConfigService? startupConfigService = null)
        {
            var settingsRepo = new EfApplicationSettingsRepository(db);
            var apiConfigRepo = new EfApiConfigurationRepository(db);
            var downloadClientRepo = new EfDownloadClientConfigurationRepository(db);
            return new ConfigurationService(
                settingsRepo, apiConfigRepo, downloadClientRepo, logger,
                userService ?? new Mock<IUserService>().Object,
                startupConfigService ?? new Mock<IStartupConfigService>().Object);
        }

        [Fact]
        public async Task SaveApplicationSettings_PersistsChanges()
        {
            var testOutputPath = FileUtils.GetAbsolutePath("test-output");
            var partialUpdatePath = FileUtils.GetAbsolutePath("partial-update");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ListenArrDbContext>(opts => opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));

            var provider = services.BuildServiceProvider(validateScopes: true);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<ConfigurationService>>();

            var svc = BuildService(db, logger);

            var settings = await svc.GetApplicationSettingsAsync();
            settings.OutputPath = testOutputPath;
            settings.ShowCompletedExternalDownloads = true;
            settings.EnabledNotificationTriggers = new System.Collections.Generic.List<string> { "book-added", "book-completed" };
            settings.Webhooks = new System.Collections.Generic.List<WebhookConfiguration>
            {
                new WebhookConfiguration { Name = "UnitWebhook", Url = "https://example.test/webhook", Type = "Zapier" }
            };

            await svc.SaveApplicationSettingsAsync(settings);

            var saved = await svc.GetApplicationSettingsAsync();

            Assert.Equal(testOutputPath, saved.OutputPath);
            Assert.True(saved.ShowCompletedExternalDownloads);
            Assert.NotNull(saved.EnabledNotificationTriggers);
            Assert.Contains("book-completed", saved.EnabledNotificationTriggers);
            Assert.NotNull(saved.Webhooks);
            Assert.Single(saved.Webhooks!);
            Assert.Equal("UnitWebhook", saved.Webhooks![0].Name);

            var partial = new ApplicationSettings { Id = 1, OutputPath = partialUpdatePath };
            await svc.SaveApplicationSettingsAsync(partial);

            var afterPartial = await svc.GetApplicationSettingsAsync();
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
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ListenArrDbContext>(opts => opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));

            var provider = services.BuildServiceProvider(validateScopes: true);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();

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

            var svc = BuildService(db, logger);

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

            var svc = BuildService(db, logger);

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
