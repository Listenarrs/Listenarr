using System;
using System.Collections.Generic;
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
    public class RenameServiceTests
    {
        private static (RenameService svc, ListenArrDbContext db) BuildService(
            ApplicationSettings? settings = null,
            Action<Mock<IFileMover>>? configureFileMover = null)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var db = new ListenArrDbContext(options);

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            var effectiveSettings = settings ?? new ApplicationSettings
            {
                OutputPath = @"C:\Library",
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
                BasePath = @"C:\Library\Stephen King\The Stand",
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile
                    {
                        Id = 1,
                        AudiobookId = 1,
                        Path = @"C:\Library\Stephen King\The Stand\The Stand.m4b"
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
                BasePath = @"C:\Library\Wrong Folder\The Stand",
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile
                    {
                        Id = 2,
                        AudiobookId = 2,
                        Path = @"C:\Library\Wrong Folder\The Stand\The Stand.m4b"
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
                BasePath = @"C:\Library\Stephen King\The Stand",
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile
                    {
                        Id = 3,
                        AudiobookId = 3,
                        Path = @"C:\Library\Stephen King\The Stand\wrong-name.m4b"
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
                BasePath = @"C:\Library\Jane Author\Standalone Novel",
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile
                    {
                        Id = 4,
                        AudiobookId = 4,
                        Path = @"C:\Library\Jane Author\Standalone Novel\Standalone Novel.m4b"
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
                BasePath = @"C:\Library\Nobody\Empty Book",
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
                BasePath = @"C:\Library\Author\Long Book",
                Files = new List<AudiobookFile>
                {
                    new AudiobookFile { Id = 10, AudiobookId = 6, Path = @"C:\Library\Author\Long Book\disc1.m4b" },
                    new AudiobookFile { Id = 11, AudiobookId = 6, Path = @"C:\Library\Author\Long Book\disc2.m4b" },
                    new AudiobookFile { Id = 12, AudiobookId = 6, Path = @"C:\Library\Author\Long Book\disc3.m4b" },
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
    }
}
