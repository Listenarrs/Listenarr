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
using Xunit;

namespace Listenarr.BackendIntegrationTests;

[Trait("Name", "SettingsPersistenceTests")]
[Trait("Category", "BackendIntegration")]
public sealed class SettingsPersistenceTests : BackendIntegrationTests
{
    private const string SettingsPath = "/api/v1/configuration/settings";

    [Fact]
    public async Task SaveSettings_ChangeIsReadBack()
    {
        var settings = (await Host.GetJsonAsync(SettingsPath)).AsObject();
        Assert.False((bool)settings["enableNotifications"]!);

        settings["enableNotifications"] = true;
        var saved = await Host.SendJsonAsync(HttpMethod.Post, SettingsPath, settings);
        Assert.True((bool)saved["enableNotifications"]!);

        var persisted = await Host.GetJsonAsync(SettingsPath);
        Assert.True((bool)persisted["enableNotifications"]!);
    }

    [Fact]
    public async Task SaveSettings_ChangeSurvivesHostRestart()
    {
        var settings = (await Host.GetJsonAsync(SettingsPath)).AsObject();
        settings["enableNotifications"] = true;
        await Host.SendJsonAsync(HttpMethod.Post, SettingsPath, settings);

        await Host.RestartOnSameRootAsync();

        var persisted = await Host.GetJsonAsync(SettingsPath);
        Assert.True((bool)persisted["enableNotifications"]!);
    }
}
