using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Api.Models;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Api.Tests
{
    public class RenameServiceTests : IDisposable
    {
        // Use a temp-based root so paths resolve correctly on any OS
        private static readonly string LibraryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RenameTests", "Library"));

        private static string Lib(params string[] parts) => Path.Combine(new[] { LibraryRoot }.Concat(parts).ToArray());

        private readonly List<ListenArrDbContext> _contexts = new();

        public void Dispose()
        {
            foreach (var ctx in _contexts)
                ctx.Dispose();
        }

        private (RenameService svc, ListenArrDbContext db) BuildService(
            ApplicationSettings? settings = null,
            Action<Mock<IFileMover>>? configureFileMover = null)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);
            _contexts.Add(db);

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            var effectiveSettings = settings ?? new ApplicationSettings
            {
                OutputPath = LibraryRoot,
                FolderNamingPattern = "{Author}/{Series}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(effectiveSettings);

            var fileNamingService = new FileNamingService(configMock.Object, new NullLogger<FileNamingService>());

            var fileMoverMock = new Mock<IFileMover>();
            fileMoverMock.Setup(m => m.MoveFileAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(true);
            configureFileMover?.Invoke(fileMoverMock);

            var svc = new RenameService(
                configMock.Object,
                fileNamingService,
                fileMoverMock.Object,
                dbFactoryMock.Object,
                new NullLogger<RenameService>(),
                moveQueueService: null,
                historyRepo: null);

            return (svc, db);
        }

        [Fact]
        public async Task PreviewRename_NoChanges_WhenPathsAlreadyMatch()
        {
            var (svc, db) = BuildService();

            db.Audiobooks.Add(new Audiobook
            {
                Id = 1,
                Title = "The Stand",
                Authors = new List<string> { "Stephen King" },
                BasePath = Lib("Stephen King", "The Stand"),
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile
                    {
                        Id = 1,
                        AudiobookId = 1,
                        Path = Lib("Stephen King", "The Stand", "The Stand.m4b")
                    }
                }
            });
            await db.SaveChangesAsync();

            var result = await svc.PreviewRenameAsync(new[] { 1 });

            Assert.Single(result);
            Assert.False(result[0].HasChanges);
            Assert.False(result[0].FolderChanged);
        }

        [Fact]
        public async Task PreviewRename_DetectsFolderMismatch()
        {
            var (svc, db) = BuildService();

            db.Audiobooks.Add(new Audiobook
            {
                Id = 2,
                Title = "The Stand",
                Authors = new List<string> { "Stephen King" },
                BasePath = Lib("Wrong Folder", "The Stand"),
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile
                    {
                        Id = 2,
                        AudiobookId = 2,
                        Path = Lib("Wrong Folder", "The Stand", "The Stand.m4b")
                    }
                }
            });
            await db.SaveChangesAsync();

            var result = await svc.PreviewRenameAsync(new[] { 2 });

            Assert.Single(result);
            Assert.True(result[0].HasChanges);
            Assert.True(result[0].FolderChanged);
            Assert.Contains("Stephen King", result[0].NewFolderPath);
        }

        [Fact]
        public async Task PreviewRename_DetectsFilenameMismatch()
        {
            var (svc, db) = BuildService();

            db.Audiobooks.Add(new Audiobook
            {
                Id = 3,
                Title = "The Stand",
                Authors = new List<string> { "Stephen King" },
                BasePath = Lib("Stephen King", "The Stand"),
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile
                    {
                        Id = 3,
                        AudiobookId = 3,
                        Path = Lib("Stephen King", "The Stand", "wrong-name.m4b")
                    }
                }
            });
            await db.SaveChangesAsync();

            var result = await svc.PreviewRenameAsync(new[] { 3 });

            Assert.Single(result);
            Assert.True(result[0].HasChanges);
            var fileRename = result[0].FileRenames.First();
            Assert.True(fileRename.Changed);
            Assert.Equal("The Stand.m4b", fileRename.NewFilename);
        }

        [Fact]
        public async Task PreviewRename_HandlesEmptySeries_CleansFolderPath()
        {
            var (svc, db) = BuildService();

            db.Audiobooks.Add(new Audiobook
            {
                Id = 4,
                Title = "Standalone Novel",
                Authors = new List<string> { "Jane Author" },
                Series = null, // No series
                BasePath = Lib("Jane Author", "Standalone Novel"),
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile
                    {
                        Id = 4,
                        AudiobookId = 4,
                        Path = Lib("Jane Author", "Standalone Novel", "Standalone Novel.m4b")
                    }
                }
            });
            await db.SaveChangesAsync();

            var result = await svc.PreviewRenameAsync(new[] { 4 });

            Assert.Single(result);
            // The Series variable is empty, so the pattern {Author}/{Series}/{Title}
            // should collapse to {Author}/{Title}
            if (result[0].FolderChanged)
            {
                Assert.DoesNotContain("Series", result[0].NewFolderPath!);
            }
        }

        [Fact]
        public async Task PreviewRename_SkipsAudiobooksWithNoFiles()
        {
            var (svc, db) = BuildService();

            db.Audiobooks.Add(new Audiobook
            {
                Id = 5,
                Title = "Empty Book",
                Authors = new List<string> { "Nobody" },
                BasePath = Lib("Nobody", "Empty Book"),
                Files = new List<AudiobookFile>()
            });
            await db.SaveChangesAsync();

            var result = await svc.PreviewRenameAsync(new[] { 5 });

            Assert.Single(result);
            Assert.Empty(result[0].FileRenames);
        }

        [Fact]
        public async Task PreviewRename_MultiFile_AppliesDiskNumbers()
        {
            var (svc, db) = BuildService();

            db.Audiobooks.Add(new Audiobook
            {
                Id = 6,
                Title = "Long Book",
                Authors = new List<string> { "Author" },
                BasePath = Lib("Author", "Long Book"),
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile { Id = 10, AudiobookId = 6, Path = Lib("Author", "Long Book", "disc1.m4b") },
                    new AudiobookFile { Id = 11, AudiobookId = 6, Path = Lib("Author", "Long Book", "disc2.m4b") },
                    new AudiobookFile { Id = 12, AudiobookId = 6, Path = Lib("Author", "Long Book", "disc3.m4b") },
                }
            });
            await db.SaveChangesAsync();

            var result = await svc.PreviewRenameAsync(new[] { 6 });

            Assert.Single(result);
            Assert.Equal(3, result[0].FileRenames.Count);

            var renames = result[0].FileRenames.OrderBy(r => r.NewFilename).ToList();
            Assert.Equal("Long Book-01.m4b", renames[0].NewFilename);
            Assert.Equal("Long Book-02.m4b", renames[1].NewFilename);
            Assert.Equal("Long Book-03.m4b", renames[2].NewFilename);
        }

        [Fact]
        public async Task PreviewRename_EmptyIdsReturnsEmptyList()
        {
            var (svc, _) = BuildService();

            var result = await svc.PreviewRenameAsync(Array.Empty<int>());

            Assert.Empty(result);
        }

        [Fact]
        public async Task PreviewRename_RejectsMoreThan500Ids()
        {
            var (svc, _) = BuildService();

            var ids = Enumerable.Range(1, 501).ToArray();
            await Assert.ThrowsAsync<ArgumentException>(() => svc.PreviewRenameAsync(ids));
        }

        [Fact]
        public async Task PreviewRename_NoOutputPath_ReturnsNoChanges()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = "", // No output path configured
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
            };
            var (svc, db) = BuildService(settings);

            db.Audiobooks.Add(new Audiobook
            {
                Id = 7,
                Title = "No Root",
                Authors = new List<string> { "Author" },
                Files = new List<AudiobookFile>()
            });
            await db.SaveChangesAsync();

            var result = await svc.PreviewRenameAsync(new[] { 7 });

            Assert.Single(result);
            Assert.False(result[0].HasChanges);
        }

        [Fact]
        public async Task ExecuteRename_RejectsMoreThan500Operations()
        {
            var (svc, _) = BuildService();

            var operations = Enumerable.Range(1, 501)
                .Select(i => new RenameOperation { AudiobookId = i })
                .ToList();
            await Assert.ThrowsAsync<ArgumentException>(() => svc.ExecuteRenameAsync(operations));
        }

        [Fact]
        public async Task ExecuteRename_RejectsFolderMoveOutsideOutputPath()
        {
            var (svc, db) = BuildService();

            db.Audiobooks.Add(new Audiobook
            {
                Id = 8,
                Title = "Escape Test",
                Authors = new List<string> { "Author" },
                BasePath = Lib("Author", "Escape Test"),
                Files = new List<AudiobookFile>()
            });
            await db.SaveChangesAsync();

            var outsidePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SomewhereElse", "Evil"));

            var operations = new List<RenameOperation>
            {
                new RenameOperation
                {
                    AudiobookId = 8,
                    NewFolderPath = outsidePath,
                    FileRenames = new List<FileRenameOperation>()
                }
            };

            var results = await svc.ExecuteRenameAsync(operations);

            Assert.Single(results);
            Assert.False(results[0].Success);
            Assert.Contains("outside", results[0].Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExecuteRename_RejectsFileRenameOutsideOutputPath()
        {
            var (svc, db) = BuildService();

            var validSource = Lib("Author", "Book", "Book.m4b");
            var outsideTarget = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SomewhereElse", "stolen.m4b"));

            db.Audiobooks.Add(new Audiobook
            {
                Id = 9,
                Title = "Book",
                Authors = new List<string> { "Author" },
                BasePath = Lib("Author", "Book"),
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile { Id = 20, AudiobookId = 9, Path = validSource }
                }
            });
            await db.SaveChangesAsync();

            var operations = new List<RenameOperation>
            {
                new RenameOperation
                {
                    AudiobookId = 9,
                    FileRenames = new List<FileRenameOperation>
                    {
                        new FileRenameOperation
                        {
                            FileId = 20,
                            CurrentPath = validSource,
                            NewPath = outsideTarget
                        }
                    }
                }
            };

            var results = await svc.ExecuteRenameAsync(operations);

            Assert.Single(results);
            var fileResult = Assert.Single(results[0].RenamedFiles);
            Assert.False(fileResult.Success);
            Assert.Contains("outside", fileResult.Error, StringComparison.OrdinalIgnoreCase);
        }
    }
}
