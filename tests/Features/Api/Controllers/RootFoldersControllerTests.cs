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
using Listenarr.Api.Controllers;
using Listenarr.Domain.Models;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Interfaces;
using Listenarr.Infrastructure.Persistence.Repositories;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class RootFoldersControllerTests
    {
        private class FakeUnmatchedQueue : IUnmatchedScanQueueService
        {
            public System.Threading.Channels.ChannelReader<UnmatchedScanJob> Reader =>
                System.Threading.Channels.Channel.CreateUnbounded<UnmatchedScanJob>().Reader;
            public Task<Guid> EnqueueAsync(string rootFolderPath) => Task.FromResult(Guid.NewGuid());
            public bool TryGetJob(Guid id, out UnmatchedScanJob? job) { job = null; return false; }
            public void UpdateJob(Guid id, string status, List<UnmatchedFileResult>? results = null, string? error = null) { }
            public bool TryGetLastJobForPath(string rootFolderPath, out UnmatchedScanJob? job) { job = null; return false; }
        }

        private static readonly IUnmatchedScanQueueService _fakeQueue = new FakeUnmatchedQueue();

        private static ListenArrDbContext CreateDb() =>
            new ListenArrDbContext(
                new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);

        private class FakeService : IRootFolderService
        {
            public List<RootFolder> Store { get; } = new List<RootFolder>();

            public Task<RootFolder?> GetDefaultAsync() => Task.FromResult(Store.Count > 0 ? Store.First() : null);

            public Task<List<RootFolder>> GetAllAsync() => Task.FromResult(new List<RootFolder>(Store));

            public Task<RootFolder?> GetByIdAsync(int id)
            {
                var f = Store.Find(s => s.Id == id);
                return Task.FromResult<RootFolder?>(f);
            }

            public Task<RootFolder> CreateAsync(RootFolder root)
            {
                // simulate duplicate path error
                if (Store.Exists(s => string.Equals(s.Path, root.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("A root with the same path already exists")
; root.Id = Store.Count + 1;
                Store.Add(root);
                return Task.FromResult(root);
            }

            public Task<RootFolder> UpdateAsync(RootFolder root, bool moveFiles = false, bool deleteEmptySource = true)
            {
                var idx = Store.FindIndex(s => s.Id == root.Id);
                if (idx < 0) throw new KeyNotFoundException("Root folder not found");

                // simulate invalid operation for certain paths
                if (root.Path?.Contains("/invalid/") == true) throw new InvalidOperationException("Invalid path")
;
                Store[idx] = root;
                return Task.FromResult(root);
            }

            public Task DeleteAsync(int id, int? reassignRootId = null)
            {
                var idx = Store.FindIndex(s => s.Id == id);
                if (idx < 0) throw new KeyNotFoundException("Root folder not found");

                // simulate in-use error if path contains "inuse"
                if (Store[idx].Path?.Contains("inuse") == true && reassignRootId == null)
                    throw new InvalidOperationException("Root folder in use")
;
                Store.RemoveAt(idx);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task GetAll_ReturnsAll()
        {
            var svc = new FakeService();
            svc.Store.AddRange(new[] {
                new RootFolder { Id = 1, Name = "Root1", Path = FileUtils.GetAbsolutePath("root1") },
                new RootFolder { Id = 2, Name = "Root2", Path = FileUtils.GetAbsolutePath("root2") }
            });

            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db));

            var res = await controller.GetAll();
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(res);
            var list = Assert.IsAssignableFrom<List<RootFolder>>(ok.Value);
            Assert.Equal(2, list.Count);
        }

        [Fact]
        public async Task Get_NotFound_Returns404()
        {
            var svc = new FakeService();
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db));

            var res = await controller.Get(123);
            var notFound = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(res);
            Assert.Contains("not found", notFound.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Create_DuplicatePath_ReturnsBadRequest()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R1", Path = FileUtils.GetAbsolutePath("dup") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db));

            var req = new RootFolder { Name = "New", Path = FileUtils.GetAbsolutePath("dup") };
            var res = await controller.Create(req);

            var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(res);
            Assert.Contains("same path", bad.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_IdMismatch_ReturnsBadRequest()
        {
            var svc = new FakeService();
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db));

            var req = new RootFolder { Id = 2, Name = "R", Path = FileUtils.GetAbsolutePath("p") };
            var res = await controller.Update(1, req);

            var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(res);
            Assert.Contains("Id mismatch", bad.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Update_NotFound_ReturnsNotFound()
        {
            var svc = new FakeService();
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db));

            var req = new RootFolder { Id = 99, Name = "R", Path = FileUtils.GetAbsolutePath("p") };
            var res = await controller.Update(99, req);

            var nf = Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(res);
            Assert.Contains("not found", nf.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Delete_InUseWithoutReassign_ReturnsBadRequest()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R", Path = FileUtils.GetAbsolutePath("inuse") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db));

            var res = await controller.Delete(1, null);
            var bad = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(res);
            Assert.Contains("in use", bad.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Delete_WithReassign_Succeeds()
        {
            var svc = new FakeService();
            svc.Store.Add(new RootFolder { Id = 1, Name = "R", Path = FileUtils.GetAbsolutePath("inuse") });
            svc.Store.Add(new RootFolder { Id = 2, Name = "R2", Path = FileUtils.GetAbsolutePath("r") });
            var _db = CreateDb();
            var controller = new RootFoldersController(svc, _fakeQueue, new EfAudiobookFileRepository(_db), new AudiobookRepository(_db));

            var res = await controller.Delete(1, 2);
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(res);
            Assert.Contains("Deleted", ok.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
