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
using Listenarr.Api.Services;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "AudibleServiceTitleSearchTests")]
    [Trait("Category", "AudibleService")]
    [Trait("Third-Party", "Audible")]
    public class AudibleServiceTitleSearchTests : BaseTests
    {
        [Fact]
        [Trait("Method", "SearchByTitleAsync")]
        public async Task SearchByTitleAsync_UsesKeywordCatalogSearch_ForTitleOnlyQueries()
        {
            var service = _provider.GetRequiredService<AudibleService>();
            var result = await service.SearchByTitleAsync("Project Hail Mary", page: 1, limit: 50, region: "us", language: "english");

            Assert.NotNull(result);
            var book = Assert.Single(result.Results);
            Assert.Equal("Project Hail Mary", book.Title);
        }
    }
}
