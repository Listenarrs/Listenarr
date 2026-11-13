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

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    [Trait("Name", "IndexersControllerDailyRequestLimitTests")]
    [Trait("Category", "Api")]
    public sealed class IndexersControllerDailyRequestLimitTests : BaseTests
    {
        [Theory]
        [InlineData(2000)]
        [InlineData(null)]
        public async Task Update_StoresTheDailyRequestLimit(int? limit)
        {
            var stored = await _indexerRepository.AddAsync(new IndexerBuilder()
                .WithName("Usenet")
                .WithImplementation("Newznab")
                .WithUrl("https://indexer.invalid")
                .WithDailyRequestLimit(500)
                .Build());
            var controller = MockUtils.CreateIndexersController(_provider, new HttpClientHandler());
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            var edit = new IndexerBuilder()
                .WithName("Usenet")
                .WithImplementation("Newznab")
                .WithUrl("https://indexer.invalid")
                .WithDailyRequestLimit(limit)
                .Build();

            Assert.IsType<OkObjectResult>(await controller.Update(stored.Id, edit));

            using var scope = _provider.CreateScope();
            var reloaded = await scope.ServiceProvider.GetRequiredService<IIndexerRepository>().GetByIdAsync(stored.Id);
            Assert.Equal(limit, reloaded!.DailyRequestLimit);
        }
    }
}
