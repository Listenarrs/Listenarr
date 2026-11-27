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

[Trait("Name", "HostIsolationTests")]
[Trait("Category", "BackendIntegration")]
public sealed class HostIsolationTests : BackendIntegrationTests
{
    [Fact]
    public async Task Host_ContentRootIsItsOwnTestRoot_AlsoAfterARestart()
    {
        // Test classes run in parallel: hosts sharing a content root can load appsettings.json
        // while another host is still writing it.
        var testRoot = Path.GetDirectoryName(Host.ImportPath)!;

        Assert.Equal(Path.GetFullPath(testRoot), Host.ContentRootPath);
        Assert.True(File.Exists(Path.Join(testRoot, "config", "appsettings", "appsettings.json")));

        await Host.RestartOnSameRootAsync();

        Assert.Equal(Path.GetFullPath(testRoot), Host.ContentRootPath);
    }
}
