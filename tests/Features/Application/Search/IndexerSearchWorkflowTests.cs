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

namespace Listenarr.Tests.Features.Application.Search
{
    [Trait("Area", "Search")]
    [Trait("Name", "IndexerSearchWorkflowTests")]
    [Trait("Category", "IndexerSearchWorkflow")]
    public class IndexerSearchWorkflowTests : BaseTests
    {
        [Fact]
        [Trait("Method", "SearchIndexersAsync")]
        [Trait("Scenario", "NoIndexersConfiguredReturnsEmptyNotMockResults")]
        public async Task SearchIndexers_NoIndexersConfigured_ReturnsEmptyWithoutSyntheticResults()
        {
            // Given
            var workflow = _provider.GetRequiredService<IndexerSearchWorkflow>();
            Assert.Empty(await _indexerRepository.GetEnabledAsync(isAutomaticSearch: false));

            // When
            var results = await workflow.SearchIndexersAsync("Dune");

            // Then
            Assert.Empty(results);
        }
    }
}
