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
using Microsoft.Extensions.Hosting;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Infrastructure.DependencyInjection
{
    [Trait("Area", "DependencyInjection")]
    [Trait("Name", "DependencyInjectionTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class DependencyInjectionTests(ListenarrWebApplicationFactory factory)
        : BaseTests, IClassFixture<ListenarrWebApplicationFactory>
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

        [Fact]
        public void DisabledHostedServices_StillResolveSharedFilesystemSafetyServices()
        {
            var firstCoordinator = factory.Services.GetRequiredService<IFilesystemMutationCoordinator>();
            var secondCoordinator = factory.Services.GetRequiredService<IFilesystemMutationCoordinator>();

            Assert.Same(firstCoordinator, secondCoordinator);
            Assert.NotNull(factory.Services.GetRequiredService<IRootFolderRelocationService>());
            Assert.NotNull(factory.Services.GetRequiredService<IMoveQueueService>());
            Assert.DoesNotContain(
                factory.Services.GetServices<IHostedService>(),
                service => service is MoveBackgroundService);
        }

        [Fact]
        public async Task DisabledHostedServices_RootMutationStillRejectsActiveRelocationBoundary()
        {
            var sourcePath = Path.Join(Path.GetTempPath(), $"disabled-workers-source-{Guid.NewGuid():N}");
            var targetPath = Path.Join(Path.GetTempPath(), $"disabled-workers-target-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sourcePath);
            try
            {
                var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
                await using (var db = await dbFactory.CreateDbContextAsync())
                {
                    var root = new RootFolder { Name = "Active relocation", Path = sourcePath };
                    db.RootFolders.Add(root);
                    await db.SaveChangesAsync();
                    db.RootFolderRelocations.Add(new RootFolderRelocation
                    {
                        RootFolderId = root.Id,
                        ActiveRootFolderId = root.Id,
                        SourcePath = sourcePath,
                        TargetPath = targetPath,
                        DesiredName = root.Name,
                        Status = RootFolderRelocationStatus.Running
                    });
                    await db.SaveChangesAsync();
                }

                using var scope = factory.Services.CreateScope();
                var rootFolderService = scope.ServiceProvider.GetRequiredService<IRootFolderService>();

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    rootFolderService.CreateAsync(new RootFolder
                    {
                        Name = "Overlapping root",
                        Path = Path.Join(sourcePath, "nested")
                    }));

                Assert.Contains("relocation boundary", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(sourcePath, recursive: true);
            }
        }
    }
}
