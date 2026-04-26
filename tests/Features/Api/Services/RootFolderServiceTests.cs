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
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Domain.Utils;

namespace Listenarr.Tests.Features.Api.Services
{
    public class RootFolderServiceTests
    {
        private string booksPath = FileUtils.GetAbsolutePath("books");
        private string rootPath = FileUtils.GetAbsolutePath("root");
        private string newRootPath = FileUtils.GetAbsolutePath("newroot");
        private string rootAuthorTitlePath = FileUtils.GetAbsolutePath("root", "Author", "Title");
        private string newRootAuthorTitlePath = FileUtils.GetAbsolutePath("newroot", "Author", "Title");

        private readonly ITestOutputHelper _output;
        public RootFolderServiceTests(ITestOutputHelper output) { _output = output; }

        [Fact]
        public async Task Create_Throws_WhenPathDuplicate()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            db.RootFolders.Add(new RootFolder { Name = "A", Path = booksPath });
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(new RootFolder { Name = "B", Path = booksPath }));
        }

        [Fact]
        public async Task Delete_Throws_WhenReferencedWithoutReassign()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "A", Path = booksPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "T", BasePath = booksPath });
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var svc = new RootFolderService(repo, null!);

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync(root.Id));
        }

        [Fact]
        public async Task Update_RenameWithoutMove_UpdatesAudiobookBasePaths()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "A1", BasePath = rootAuthorTitlePath });
            db.Audiobooks.Add(new Audiobook { Title = "A2", BasePath = rootPath });
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());
            var logger = new TestLogger<RootFolderService>(_output);
            var svc = new RootFolderService(repo, logger);

            using (var pre = new ListenArrDbContext(options))
            {
                var dumpPre = string.Join("; ", pre.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                _output.WriteLine("Before update: " + dumpPre);
            }

            await svc.UpdateAsync(new RootFolder { Id = root.Id, Name = "R2", Path = newRootPath }, moveFiles: false);

            using (var verifyDb = new ListenArrDbContext(options))
            {
                var dumpAfter = string.Join("; ", verifyDb.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                _output.WriteLine("After update: " + dumpAfter);

                var a1 = verifyDb.Audiobooks.First(a => a.Title == "A1").BasePath;
                var a2 = verifyDb.Audiobooks.First(a => a.Title == "A2").BasePath;
                if (a1 != newRootAuthorTitlePath || a2 != newRootPath)
                {
                    var dump = string.Join("; ", verifyDb.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                    throw new Xunit.Sdk.XunitException($"Unexpected audiobook base paths after root update. Dump: {dump}");
                }
                Assert.Equal(newRootAuthorTitlePath, a1);
                Assert.Equal(newRootPath, a2);
            }
        }

        [Fact]
        public async Task Update_RenameWithMove_EnqueuesMovesAndUpdatesDB()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var root = new RootFolder { Name = "R", Path = rootPath };
            db.RootFolders.Add(root);
            var ab1 = new Audiobook { Id = 1, Title = "A1", BasePath = rootAuthorTitlePath };
            var ab2 = new Audiobook { Id = 2, Title = "A2", BasePath = rootPath };
            db.Audiobooks.AddRange(ab1, ab2);
            await db.SaveChangesAsync();

            var dbFactory = new TestDbFactory(options);
            var repo = new EfRootFolderRepository(dbFactory, Mock.Of<ILogger<EfRootFolderRepository>>());

            var mockMove = new Moq.Mock<IMoveQueueService>();
            mockMove.Setup(m => m.EnqueueMoveAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Guid.NewGuid());

            var logger = new TestLogger<RootFolderService>(_output);
            var svc = new RootFolderService(repo, logger, mockMove.Object);

            using (var pre = new ListenArrDbContext(options))
            {
                var dumpPre = string.Join("; ", pre.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                _output.WriteLine("Before update (with move): " + dumpPre);
            }

            await svc.UpdateAsync(new RootFolder { Id = root.Id, Name = "R2", Path = newRootPath }, moveFiles: true);

            using (var verifyDb = new ListenArrDbContext(options))
            {
                var dumpAfter = string.Join("; ", verifyDb.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                _output.WriteLine("After update (with move): " + dumpAfter);

                var a1 = verifyDb.Audiobooks.First(a => a.Title == "A1").BasePath;
                var a2 = verifyDb.Audiobooks.First(a => a.Title == "A2").BasePath;
                if (a1 != newRootAuthorTitlePath || a2 != newRootPath)
                {
                    var dump = string.Join("; ", verifyDb.Audiobooks.Select(a => $"{a.Title} => {a.BasePath}"));
                    throw new Xunit.Sdk.XunitException($"Unexpected audiobook base paths after root update (with move). Dump: {dump}");
                }
                Assert.Equal(newRootAuthorTitlePath, a1);
                Assert.Equal(newRootPath, a2);
            }

            mockMove.Verify(m => m.EnqueueMoveAsync(1, newRootAuthorTitlePath, rootAuthorTitlePath), Times.Once);
            mockMove.Verify(m => m.EnqueueMoveAsync(2, newRootPath, rootPath), Times.Once);
        }

        private class TestDbFactory : IDbContextFactory<ListenArrDbContext>
        {
            private readonly DbContextOptions<ListenArrDbContext> _options;
            public TestDbFactory(DbContextOptions<ListenArrDbContext> options) { _options = options; }
            public Task<ListenArrDbContext> CreateDbContextAsync() => Task.FromResult(new ListenArrDbContext(_options));
            public ListenArrDbContext CreateDbContext() => new ListenArrDbContext(_options);
        }

        private class TestLogger<T> : ILogger<T>
        {
            private readonly ITestOutputHelper _out;
            public TestLogger(ITestOutputHelper output) { _out = output; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _out.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception != null ? " Exception: " + exception : "")}");
            }
        }
    }
}
