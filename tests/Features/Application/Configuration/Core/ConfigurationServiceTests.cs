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
using Microsoft.EntityFrameworkCore;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Application.Configuration.Core
{
    [Trait("Name", "ConfigurationServiceTests")]
    [Trait("Category", "ConfigurationService")]
    public class ConfigurationServiceTests : BaseTests
    {
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

            var svc = _provider.GetRequiredService<IConfigurationService>();

            var settings = await svc.GetApplicationSettingsAsync();
            settings.OutputPath = testOutputPath;
            settings.ShowCompletedExternalDownloads = true;
            settings.EnabledNotificationTriggers = ["book-added", "book-completed"];
            settings.Webhooks =
            [
                new() { Name = "UnitWebhook", Url = "https://example.test/webhook", Type = "Zapier" }
            ];

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
            var settings = new ApplicationSettingsBuilder().Build();
            await _applicationSettingsRepository.SaveAsync(settings);

            settings.Webhooks =
            [
                new() { Name = "DirectWebhook", Url = "https://example.test/direct", Type = "Zapier" }
            ];
            await _applicationSettingsRepository.SaveAsync(settings);

            var reloaded = await _applicationSettingsRepository.GetAsync();

            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded!.Webhooks);
            Assert.Single(reloaded.Webhooks!);
            Assert.Equal("DirectWebhook", reloaded.Webhooks![0].Name);
        }

        [Fact]
        public async Task ProwlarrImportSettings_ApiKey_IsEncryptedAtRest_AndRecoveredForServerUse()
        {
            var svc = _provider.GetRequiredService<IConfigurationService>();

            await svc.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "super-secret-prowlarr-key",
                TagFilter = "audiobooks",
            });

            var stored = await _applicationSettingsRepository.GetAsync();
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
            var svc = _provider.GetRequiredService<IConfigurationService>();

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

            var stored = await _applicationSettingsRepository.GetAsync();
            Assert.False(string.IsNullOrWhiteSpace(stored.ProwlarrApiKeyEncrypted));
        }

        [Fact]
        public async Task SaveApplicationSettings_AdminProvisioningFailure_PropagatesToCaller()
        {
            // When the caller supplies admin credentials but the user-service
            // can't honour the request (password policy violation, repo I/O
            // error, race with a concurrent admin write), the failure must
            // reach the caller. SettingsView.saveSettings() persists
            // AuthenticationRequired=true *after* the call to
            // SaveApplicationSettingsAsync; if the admin failure is swallowed
            // here, the operator ends up with an instance that requires
            // login and has no working admin — a hard lockout.
            //
            // Regression coverage for the `kevinheneveld:fix/auth-admin-credentials-always-visible`
            // upstream PR review feedback.
            var failingUserService = new Mock<IUserService>(MockBehavior.Strict);
            failingUserService.Setup(u => u.GetByUsernameAsync(It.IsAny<string>()))
                .ReturnsAsync((User?)null);
            failingUserService.Setup(u => u.CreateUserAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>()))
                .ThrowsAsync(new InvalidOperationException("password rejected by policy"));

            Init(b => b.WithScoped<IUserService>(_ => failingUserService.Object));

            var svc = _provider.GetRequiredService<IConfigurationService>();
            var settings = await svc.GetApplicationSettingsAsync();
            settings.AdminUsername = "admin";
            settings.AdminPassword = "weakpass";
            // Bundle a non-admin change in the same payload so we can verify
            // it still lands — non-admin settings are saved before the admin
            // block, and that ordering is intentional.
            settings.OutputPath = FileUtils.GetAbsolutePath("admin-fail-output");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SaveApplicationSettingsAsync(settings));
            Assert.Equal("password rejected by policy", ex.Message);

            // Non-admin changes saved before the admin block remain — the
            // settings row write is intentionally outside the admin try/catch.
            var afterFail = await svc.GetApplicationSettingsAsync();
            Assert.Equal(FileUtils.GetAbsolutePath("admin-fail-output"), afterFail.OutputPath);

            failingUserService.Verify(
                u => u.CreateUserAsync("admin", "weakpass", null, true),
                Times.Once);
        }

        [Fact]
        public async Task SaveApplicationSettings_NoAdminCredentials_DoesNotInvokeUserService()
        {
            // Carveout check: when no credentials are supplied (the common
            // "I'm just updating notification triggers" path), the admin
            // block must remain a silent skip — neither invoking the user
            // service nor throwing.
            var userService = new Mock<IUserService>(MockBehavior.Strict);

            Init(b => b.WithScoped<IUserService>(_ => userService.Object));

            var svc = _provider.GetRequiredService<IConfigurationService>();
            var settings = await svc.GetApplicationSettingsAsync();
            settings.AdminUsername = null;
            settings.AdminPassword = null;
            settings.OutputPath = FileUtils.GetAbsolutePath("no-creds-output");

            await svc.SaveApplicationSettingsAsync(settings);

            userService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task SaveStartupConfig_RefusesAuthEnableTransition_WhenNoAdminExists()
        {
            // Defense-in-depth backstop for the auth-enable lockout. The
            // SaveApplicationSettings throw-on-failure only covers the case
            // where credentials were *supplied* and rejected; the FE strips
            // blank fields before save, so a user can tick "Enable login
            // screen" with empty (or username-only) credentials, the admin
            // block silently no-ops, and without this check the startup
            // config would still be persisted with AuthenticationRequired=true
            // — locking the operator out of an admin-less instance.
            //
            // Regression coverage for the upstream #623 review follow-up.
            var emptyAdminUserService = new Mock<IUserService>();
            emptyAdminUserService.Setup(u => u.GetAdminUsersAsync())
                .ReturnsAsync(new List<User>());

            // Current startup config must be present and have auth *off* so
            // the new save constitutes a transition from disabled to enabled.
            var currentConfigDisabled = new Mock<IStartupConfigService>();
            currentConfigDisabled.Setup(s => s.GetConfig())
                .Returns(new StartupConfig { AuthenticationRequired = "false" });

            Init(b => b
                .WithScoped<IUserService>(_ => emptyAdminUserService.Object)
                .WithSingleton(currentConfigDisabled.Object));

            var svc = _provider.GetRequiredService<IConfigurationService>();
            var startup = new StartupConfig { AuthenticationRequired = "true" };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SaveStartupConfigAsync(startup));
            Assert.Contains("Cannot enable the login screen", ex.Message);

            emptyAdminUserService.Verify(u => u.GetAdminUsersAsync(), Times.Once);
            // The file write must NOT have been reached.
            currentConfigDisabled.Verify(s => s.SaveAsync(It.IsAny<StartupConfig>()), Times.Never);
        }

        [Fact]
        public async Task SaveStartupConfig_AllowsAuthEnableTransition_WhenAdminExists()
        {
            // The typical "supply credentials + enable login in the same save"
            // flow runs SaveApplicationSettings (which creates the admin user)
            // before SaveStartupConfig. By the time this check fires the
            // admin row exists, so the backstop passes through.
            var withAdminUserService = new Mock<IUserService>();
            withAdminUserService.Setup(u => u.GetAdminUsersAsync())
                .ReturnsAsync(new List<User>
                {
                    new() { Username = "admin", IsAdmin = true },
                });

            var currentConfigDisabled = new Mock<IStartupConfigService>();
            currentConfigDisabled.Setup(s => s.GetConfig())
                .Returns(new StartupConfig { AuthenticationRequired = "false" });

            Init(b => b
                .WithScoped<IUserService>(_ => withAdminUserService.Object)
                .WithSingleton(currentConfigDisabled.Object));

            var svc = _provider.GetRequiredService<IConfigurationService>();
            var startup = new StartupConfig { AuthenticationRequired = "true" };

            await svc.SaveStartupConfigAsync(startup);

            withAdminUserService.Verify(u => u.GetAdminUsersAsync(), Times.Once);
            currentConfigDisabled.Verify(s => s.SaveAsync(startup), Times.Once);
        }

        [Fact]
        public async Task SaveStartupConfig_SkipsAdminCheck_WhenAuthAlreadyEnabled()
        {
            // Once auth is already on, the admin must already exist (or the
            // transition check above wouldn't have let it land), so every
            // subsequent unrelated save — API key regenerations, port
            // changes, log-level tweaks — must NOT re-query the admin list.
            // This keeps the backstop scoped to the lockout vector Robbie
            // identified during first-time setup, and avoids breaking the
            // session-cookie integration tests that stub auth-on factories
            // without populating IUserService.
            var userService = new Mock<IUserService>(MockBehavior.Strict);
            var currentConfigEnabled = new Mock<IStartupConfigService>();
            currentConfigEnabled.Setup(s => s.GetConfig())
                .Returns(new StartupConfig { AuthenticationRequired = "true" });

            Init(b => b
                .WithScoped<IUserService>(_ => userService.Object)
                .WithSingleton(currentConfigEnabled.Object));

            var svc = _provider.GetRequiredService<IConfigurationService>();

            await svc.SaveStartupConfigAsync(new StartupConfig
            {
                AuthenticationRequired = "true",
                ApiKey = "regenerated-key",
            });

            userService.VerifyNoOtherCalls();
            currentConfigEnabled.Verify(s => s.SaveAsync(It.IsAny<StartupConfig>()), Times.Once);
        }

        [Fact]
        public async Task SaveStartupConfig_SkipsAdminCheck_WhenAuthDisabled()
        {
            // Carveout check: the admin-count query only fires when auth is
            // actually being enabled. Persisting any startup config with
            // AuthenticationRequired=false (or blank, or any non-truthy
            // value) doesn't need an admin and must not block on one — the
            // common path is "just updating other startup fields."
            var userService = new Mock<IUserService>(MockBehavior.Strict);

            Init(b => b.WithScoped<IUserService>(_ => userService.Object));

            var svc = _provider.GetRequiredService<IConfigurationService>();

            await svc.SaveStartupConfigAsync(new StartupConfig { AuthenticationRequired = "false" });
            await svc.SaveStartupConfigAsync(new StartupConfig { AuthenticationRequired = null });
            await svc.SaveStartupConfigAsync(new StartupConfig { AuthenticationRequired = "" });

            userService.VerifyNoOtherCalls();
        }
    }
}
