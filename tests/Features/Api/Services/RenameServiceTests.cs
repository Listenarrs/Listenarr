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
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models.Enumerations;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class RenameServiceTests : IDisposable
    {
        private readonly string _tempRoot = Path.Join(Path.GetTempPath(), "ListenarrRenameTests", Guid.NewGuid().ToString("N"));
        private readonly List<ListenArrDbContext> _contexts = new();

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, true);
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{_tempRoot}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{_tempRoot}': {ex.Message}");
            }

            foreach (var context in _contexts)
            {
                context.Dispose();
            }
        }

        [Fact]
        public async Task PreviewRename_UsesExtendedMetadataVariables()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = _tempRoot,
                FolderNamingPattern = "{Author}/{Title}/{Edition}/{Narrator}/{Publisher}/{Language}/{Asin}",
                FileNamingPattern = "{Title}"
            };

            var (service, db, _) = BuildService(settings);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 1,
                Title = "Dune",
                Authors = new List<string> { "Frank Herbert" },
                Narrators = new List<string> { "Scott Brick" },
                Publisher = "Audible",
                Language = "English",
                Asin = "B000TEST",
                Edition = "Anniversary",
                BasePath = Path.Join(_tempRoot, "Wrong", "Folder"),
                Files = new List<AudiobookFile>
                {
                    new() { Id = 11, AudiobookId = 1, Path = Path.Join(_tempRoot, "Wrong", "Folder", "old-name.m4b"), Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            var previews = await service.PreviewRenameAsync(new[] { 1 });

            var preview = Assert.Single(previews);
            Assert.True(preview.HasChanges);
            Assert.Contains("Anniversary", preview.NewFolderPath);
            Assert.Contains("Scott Brick", preview.NewFolderPath);
            Assert.Contains("Audible", preview.NewFolderPath);
            Assert.Contains("English", preview.NewFolderPath);
            Assert.Contains("B000TEST", preview.NewFolderPath);
        }

        [Fact]
        public async Task PreviewRename_PreservesCustomBasePath()
        {
            var outputPath = Path.Join(_tempRoot, "library");
            var customBase = Path.Join(_tempRoot, "custom-shelf", "Dune");
            Directory.CreateDirectory(customBase);

            var settings = new ApplicationSettings
            {
                OutputPath = outputPath,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };

            var (service, db, _) = BuildService(settings);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 2,
                Title = "Dune",
                Authors = new List<string> { "Frank Herbert" },
                BasePath = customBase,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 21, AudiobookId = 2, Path = Path.Join(customBase, "wrong-name.m4b"), Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            var previews = await service.PreviewRenameAsync(new[] { 2 });

            var preview = Assert.Single(previews);
            Assert.False(preview.FolderChanged);
            Assert.Equal(NormalizePath(customBase), preview.NewFolderPath);
            Assert.All(preview.FileRenames, file => Assert.StartsWith(NormalizePath(customBase), file.NewPath!, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ExecuteRename_RejectsPathsOutsideAllowedRoots()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var bookFolder = Path.Join(libraryRoot, "Book");
            Directory.CreateDirectory(bookFolder);
            var sourcePath = Path.Join(bookFolder, "Book.m4b");
            await File.WriteAllTextAsync(sourcePath, "test");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };

            var (service, db, _) = BuildService(settings);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 3,
                Title = "Book",
                Authors = new List<string> { "Author" },
                BasePath = bookFolder,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 31, AudiobookId = 3, Path = sourcePath, Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            var results = await service.ExecuteRenameAsync(new List<RenameOperation>
            {
                new()
                {
                    AudiobookId = 3,
                    FileRenames = new List<FileRenameOperation>
                    {
                        new()
                        {
                            FileId = 31,
                            CurrentPath = sourcePath,
                            NewPath = Path.Join(_tempRoot, "outside", "Book.m4b")
                        }
                    }
                }
            });

            var result = Assert.Single(results);
            Assert.False(result.Success);
            var fileResult = Assert.Single(result.RenamedFiles);
            Assert.False(fileResult.Success);
            Assert.Contains("outside", fileResult.Error!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExecuteRename_RejectsFileIdsThatDoNotBelongToAudiobook()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var bookFolder = Path.Join(libraryRoot, "Book");
            Directory.CreateDirectory(bookFolder);
            var rogueSourcePath = Path.Join(bookFolder, "rogue-file.m4b");
            var rogueTargetPath = Path.Join(bookFolder, "moved-rogue-file.m4b");
            await File.WriteAllTextAsync(rogueSourcePath, "rogue");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };

            var (service, db, _) = BuildService(settings);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 5,
                Title = "Book",
                Authors = new List<string> { "Author" },
                BasePath = bookFolder,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 51, AudiobookId = 5, Path = Path.Join(bookFolder, "tracked-file.m4b"), Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            var results = await service.ExecuteRenameAsync(new List<RenameOperation>
            {
                new()
                {
                    AudiobookId = 5,
                    FileRenames = new List<FileRenameOperation>
                    {
                        new()
                        {
                            FileId = 999,
                            CurrentPath = rogueSourcePath,
                            NewPath = rogueTargetPath
                        }
                    }
                }
            });

            var result = Assert.Single(results);
            Assert.False(result.Success);
            var fileResult = Assert.Single(result.RenamedFiles);
            Assert.False(fileResult.Success);
            Assert.Equal("File does not belong to this audiobook.", fileResult.Error);
            Assert.True(File.Exists(rogueSourcePath));
            Assert.False(File.Exists(rogueTargetPath));
        }

        [Fact]
        public async Task ExecuteRename_RejectsSourcePathsThatDoNotMatchTrackedFile()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var bookFolder = Path.Join(libraryRoot, "Book");
            Directory.CreateDirectory(bookFolder);
            var trackedSourcePath = Path.Join(bookFolder, "tracked-file.m4b");
            var rogueSourcePath = Path.Join(bookFolder, "rogue-file.m4b");
            var rogueTargetPath = Path.Join(bookFolder, "moved-rogue-file.m4b");
            await File.WriteAllTextAsync(trackedSourcePath, "tracked");
            await File.WriteAllTextAsync(rogueSourcePath, "rogue");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };

            var (service, db, _) = BuildService(settings);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 6,
                Title = "Book",
                Authors = new List<string> { "Author" },
                BasePath = bookFolder,
                FilePath = trackedSourcePath,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 61, AudiobookId = 6, Path = trackedSourcePath, Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            var results = await service.ExecuteRenameAsync(new List<RenameOperation>
            {
                new()
                {
                    AudiobookId = 6,
                    FileRenames = new List<FileRenameOperation>
                    {
                        new()
                        {
                            FileId = 61,
                            CurrentPath = rogueSourcePath,
                            NewPath = rogueTargetPath
                        }
                    }
                }
            });

            var result = Assert.Single(results);
            Assert.False(result.Success);
            var fileResult = Assert.Single(result.RenamedFiles);
            Assert.False(fileResult.Success);
            Assert.Equal("Source path does not match the tracked audiobook file.", fileResult.Error);
            Assert.True(File.Exists(trackedSourcePath));
            Assert.True(File.Exists(rogueSourcePath));
            Assert.False(File.Exists(rogueTargetPath));
        }

        [Fact]
        public async Task ExecuteRename_RecomputesBasePathAfterPartialFileFailures()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var sourceFolder = Path.Join(libraryRoot, "Old");
            var targetFolder = Path.Join(libraryRoot, "Author", "Book");
            Directory.CreateDirectory(sourceFolder);

            var firstSourcePath = Path.Join(sourceFolder, "Part 1.m4b");
            var secondSourcePath = Path.Join(sourceFolder, "Part 2.m4b");
            var firstTargetPath = Path.Join(targetFolder, "Part 1.m4b");
            var secondTargetPath = Path.Join(targetFolder, "Part 2.m4b");
            await File.WriteAllTextAsync(firstSourcePath, "one");
            await File.WriteAllTextAsync(secondSourcePath, "two");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };

            var (service, db, dbName) = BuildService(settings, fileMover =>
            {
                fileMover.Setup(mover => mover.PerformActionOn(FileAction.Move, It.IsAny<string>(), It.Is<string>(dest => dest.EndsWith("Part 2.m4b", StringComparison.OrdinalIgnoreCase))))
                    .ReturnsAsync(false);
            });

            db.Audiobooks.Add(new Audiobook
            {
                Id = 7,
                Title = "Book",
                Authors = new List<string> { "Author" },
                BasePath = sourceFolder,
                FilePath = firstSourcePath,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 71, AudiobookId = 7, Path = firstSourcePath, Format = "m4b" },
                    new() { Id = 72, AudiobookId = 7, Path = secondSourcePath, Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            var results = await service.ExecuteRenameAsync(new List<RenameOperation>
            {
                new()
                {
                    AudiobookId = 7,
                    NewFolderPath = targetFolder,
                    FileRenames = new List<FileRenameOperation>
                    {
                        new()
                        {
                            FileId = 71,
                            CurrentPath = firstSourcePath,
                            NewPath = firstTargetPath
                        },
                        new()
                        {
                            FileId = 72,
                            CurrentPath = secondSourcePath,
                            NewPath = secondTargetPath
                        }
                    }
                }
            });

            var result = Assert.Single(results);
            Assert.False(result.Success);
            Assert.Equal(2, result.RenamedFiles.Count);
            Assert.Contains(result.RenamedFiles, item => item.FileId == 71 && item.Success);
            Assert.Contains(result.RenamedFiles, item => item.FileId == 72 && !item.Success);

            await using var verifyDb = CreateContext(dbName);
            var saved = await verifyDb.Audiobooks.Include(a => a.Files).SingleAsync(a => a.Id == 7);

            Assert.Equal(NormalizePath(libraryRoot), NormalizePath(saved.BasePath));
            Assert.NotEqual(NormalizePath(targetFolder), NormalizePath(saved.BasePath));
            Assert.Contains(saved.Files!, file => file.Id == 71 && NormalizePath(file.Path) == NormalizePath(firstTargetPath));
            Assert.Contains(saved.Files!, file => file.Id == 72 && NormalizePath(file.Path) == NormalizePath(secondSourcePath));
            Assert.True(File.Exists(firstTargetPath));
            Assert.True(File.Exists(secondSourcePath));
            Assert.False(File.Exists(secondTargetPath));
        }

        [Fact]
        public async Task ExecuteRename_MovesFileAndUpdatesDatabasePaths()
        {
            var libraryRoot = Path.Join(_tempRoot, "library");
            var sourceFolder = Path.Join(libraryRoot, "Old");
            var targetFolder = Path.Join(libraryRoot, "Author", "Book");
            Directory.CreateDirectory(sourceFolder);
            var sourcePath = Path.Join(sourceFolder, "old-name.m4b");
            var targetPath = Path.Join(targetFolder, "Book.m4b");
            await File.WriteAllTextAsync(sourcePath, "test");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };

            var (service, db, dbName) = BuildService(settings);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 4,
                Title = "Book",
                Authors = new List<string> { "Author" },
                BasePath = sourceFolder,
                FilePath = sourcePath,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 41, AudiobookId = 4, Path = sourcePath, Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            var results = await service.ExecuteRenameAsync(new List<RenameOperation>
            {
                new()
                {
                    AudiobookId = 4,
                    NewFolderPath = targetFolder,
                    FileRenames = new List<FileRenameOperation>
                    {
                        new()
                        {
                            FileId = 41,
                            CurrentPath = sourcePath,
                            NewPath = targetPath
                        }
                    }
                }
            });

            var result = Assert.Single(results);
            Assert.True(result.Success);
            Assert.True(File.Exists(targetPath));
            Assert.False(File.Exists(sourcePath));

            await using var verifyDb = CreateContext(dbName);
            var saved = await verifyDb.Audiobooks.Include(a => a.Files).SingleAsync(a => a.Id == 4);
            Assert.Equal(NormalizePath(targetFolder), NormalizePath(saved.BasePath));
            Assert.Equal(NormalizePath(targetPath), NormalizePath(saved.FilePath));
            Assert.Equal(NormalizePath(targetPath), NormalizePath(saved.Files!.Single().Path));
        }

        [Fact]
        public async Task PreviewRename_SeriesToken_UsesChosenPrimarySeries()
        {
            // Regression for #658: {Series} must fold under the user-chosen primary series,
            // even when it is not the metadata provider's default (first) series.
            var settings = new ApplicationSettings
            {
                OutputPath = _tempRoot,
                FolderNamingPattern = "{Author}/{Series}/{Title}",
                FileNamingPattern = "{Title}"
            };

            var (service, db, _) = BuildService(settings);
            var audiobook = new Audiobook
            {
                Id = 5,
                Title = "Patriot Games",
                Authors = new List<string> { "Tom Clancy" },
                BasePath = Path.Join(_tempRoot, "Wrong", "Folder"),
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new() { SeriesName = "Publication Order", SeriesNumber = "1", IsPrimary = false, SortOrder = 0 },
                    new() { SeriesName = "Chronological Order", SeriesNumber = "3", IsPrimary = true, SortOrder = 1 },
                },
                Files = new List<AudiobookFile>
                {
                    new() { Id = 51, AudiobookId = 5, Path = Path.Join(_tempRoot, "Wrong", "Folder", "old.m4b"), Format = "m4b" }
                }
            };
            // Denormalize Series/SeriesNumber from the chosen primary membership, as the app does on save.
            AudiobookSeriesMembershipHelper.ApplyPrimarySeriesFields(audiobook);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();

            var previews = await service.PreviewRenameAsync(new[] { 5 });

            var preview = Assert.Single(previews);
            Assert.True(preview.HasChanges);
            Assert.Contains("Chronological Order", preview.NewFolderPath);
            Assert.DoesNotContain("Publication Order", preview.NewFolderPath);
        }

        private (RenameService Service, ListenArrDbContext Db, string DbName) BuildService(
            ApplicationSettings settings,
            Action<Mock<IFileMover>>? configureFileMover = null)
        {
            var dbName = Guid.NewGuid().ToString();
            var db = CreateContext(dbName);

            var config = new Mock<IConfigurationService>();
            config.Setup(service => service.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var repo = new AudiobookRepository(db);
            var fileNaming = new FileNamingService(config.Object, NullLogger<FileNamingService>.Instance);
            var fileMover = new Mock<IFileMover>();
            fileMover.Setup(mover => mover.PerformActionOn(FileAction.Move, It.IsAny<string>(), It.IsAny<string>()))
                .Returns<FileAction, string, string>((action, source, dest) =>
                {
                    var dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.Move(source, dest, true);
                    return Task.FromResult(true);
                });
            fileMover.Setup(mover => mover.MoveDirectoryAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((source, dest) =>
                {
                    var parent = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    Directory.Move(source, dest);
                    return Task.FromResult(true);
                });
            configureFileMover?.Invoke(fileMover);

            var service = new RenameService(
                config.Object,
                fileNaming,
                fileMover.Object,
                repo,
                NullLogger<RenameService>.Instance);

            return (service, db, dbName);
        }

        private ListenArrDbContext CreateContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options;
            var context = new ListenArrDbContext(options);
            _contexts.Add(context);
            return context;
        }

        private static string NormalizePath(string? path) => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path);
    }
}
