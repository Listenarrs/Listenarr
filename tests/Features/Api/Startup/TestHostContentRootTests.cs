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

using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace Listenarr.Tests.Features.Api.Startup;

[Trait("Name", "TestHostContentRootTests")]
[Trait("Category", "Startup")]
public sealed class TestHostContentRootTests : BaseTests
{
    [Fact]
    public void ListenarrWebApplicationFactory_EachHost_HasItsOwnContentRootWithItsOwnConfiguration()
    {
        // Hosts sharing a content root race on appsettings.json: one can load it while another writes it.
        string firstRoot;
        string secondRoot;
        using (var first = new ListenarrWebApplicationFactory())
        using (var second = new ListenarrWebApplicationFactory())
        {
            firstRoot = first.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
            secondRoot = second.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;

            Assert.NotEqual(firstRoot, secondRoot);
            Assert.True(File.Exists(Path.Join(firstRoot, "config", "appsettings", "appsettings.json")));
            Assert.True(File.Exists(Path.Join(secondRoot, "config", "appsettings", "appsettings.json")));
        }

        Assert.False(Directory.Exists(firstRoot));
        Assert.False(Directory.Exists(secondRoot));
    }
}
