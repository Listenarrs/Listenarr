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
// csharp
using Microsoft.EntityFrameworkCore;
using Listenarr.Infrastructure.Extensions;
using Listenarr.Application.Interfaces.Repositories;

namespace Listenarr.Tests.Features.Infrastructure.Extensions
{
    public class DependencyInjectionTests
    {
        [Fact]
        public void InfrastructureRegistrations_ResolveIAudiobookRepository()
        {
            var services = new ServiceCollection();

            // Register infrastructure implementations (the extension lives in Infrastructure project).
            // Pass the InMemory provider so the extension wires up IDbContextFactory<ListenArrDbContext>.
            services.AddListenarrInfrastructure(options =>
                options.UseInMemoryDatabase("di-test-db"));

            using var sp = services.BuildServiceProvider(validateScopes: true);

            // Resolve scoped services from a created scope to satisfy DI validation rules.
            using var scope = sp.CreateScope();
            var repo = scope.ServiceProvider.GetService<IAudiobookRepository>();

            Assert.NotNull(repo);
        }
    }
}
