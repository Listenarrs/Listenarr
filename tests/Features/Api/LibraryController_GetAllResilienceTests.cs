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
using System.Net;
using Asp.Versioning.ApiExplorer;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Tests.Mocks;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Api
{
    public class LibraryController_GetAllResilienceTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public LibraryController_GetAllResilienceTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task GetAll_DoesNotFail_WhenLegacyIsbnTextExists()
        {
            var apiBasePath = ResolveApiBasePath(_factory.Services);

            int id;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var audiobook = new Audiobook
                {
                    Title = "Legacy ISBN",
                    Monitored = true
                };
                db.Audiobooks.Add(audiobook);
                await db.SaveChangesAsync();
                id = audiobook.Id;

                // Simulate pre-migration legacy TEXT value instead of JSON array.
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE Audiobooks SET Isbn = '9780306406157' WHERE Id = {0}",
                    id);
            }

            var client = _factory.CreateClient();
            var response = await client.GetAsync($"{apiBasePath}/library");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        private static string ResolveApiBasePath(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider.GetService<IApiVersionDescriptionProvider>();
            var groupName = provider?.ApiVersionDescriptions.FirstOrDefault(d => !d.IsDeprecated)?.GroupName
                ?? provider?.ApiVersionDescriptions.FirstOrDefault()?.GroupName;

            return string.IsNullOrWhiteSpace(groupName) ? "/api/v1" : $"/api/{groupName}";
        }
    }
}
