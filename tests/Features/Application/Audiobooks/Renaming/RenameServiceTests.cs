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
using Listenarr.Application.Common.Exceptions;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Renaming
{
    public class RenameServiceTests : IDisposable
    {
        private readonly string _tempRoot = OperatingSystem.IsWindows()
            ? WindowsPathTestFixture.GetRootRelativeAliasCompatiblePath(
                "ListenarrRenameTests")
            : Path.Join(
                Path.GetTempPath(),
                "ListenarrRenameTests",
                Guid.NewGuid().ToString("N"));
        private readonly List<ListenArrDbContext> _contexts = new();
        private readonly AudiobookOperationCoordinator _operationCoordinator = new();

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

            _operationCoordinator.Dispose();
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

        [Theory]
        [InlineData(FileSystemCaseSensitivity.Sensitive, true)]
        [InlineData(FileSystemCaseSensitivity.Insensitive, false)]
        public async Task PreviewRename_CaseOnlyCandidate_UsesResolvedSemantics(
            FileSystemCaseSensitivity caseSensitivity,
            bool expectedChanged)
        {
            var bookFolder = Path.Join(_tempRoot, "case-preview");
            Directory.CreateDirectory(bookFolder);
            var settings = new ApplicationSettings
            {
                OutputPath = bookFolder,
                FolderNamingPattern = string.Empty,
                FileNamingPattern = "{Title}"
            };
            var (service, db, _) = BuildService(settings, caseSensitivity: caseSensitivity);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 99,
                Title = "book",
                BasePath = bookFolder,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 991, AudiobookId = 99, Path = Path.Join(bookFolder, "BOOK.m4b"), Format = "m4b" }
                }
            });
            await db.SaveChangesAsync();

            var preview = Assert.Single(await service.PreviewRenameAsync(new[] { 99 }));
            var file = Assert.Single(preview.FileRenames);

            Assert.Equal(expectedChanged, file.Changed);
            Assert.Equal(expectedChanged, preview.HasChanges);
        }

        [LinuxFact]
        public async Task PreviewRename_AmbiguousConfiguredRoot_DoesNotBorrowConflictingAutoSemantics()
        {
            var bookFolder = Path.Join(_tempRoot, "ambiguous-case-preview");
            Directory.CreateDirectory(bookFolder);
            var ambiguousRoot = "/" + bookFolder;
            Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                ambiguousRoot,
                out _));
            var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
            rootFolders.Setup(service => service.GetAllAsync())
                .ReturnsAsync([
                    new RootFolder
                    {
                        Id = 1,
                        Name = "Legacy Ambiguous Root",
                        Path = ambiguousRoot,
                        CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                    }
                ]);
            var settings = new ApplicationSettings
            {
                OutputPath = bookFolder,
                FolderNamingPattern = string.Empty,
                FileNamingPattern = "{Title}"
            };
            var (service, db, _) = BuildService(
                settings,
                caseSensitivity: FileSystemCaseSensitivity.Sensitive,
                rootFolderServiceOverride: rootFolders.Object);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 100,
                Title = "book",
                BasePath = bookFolder,
                Files =
                [
                    new AudiobookFile
                    {
                        Id = 1001,
                        AudiobookId = 100,
                        Path = Path.Join(bookFolder, "BOOK.m4b"),
                        Format = "m4b"
                    }
                ]
            });
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PreviewRenameAsync([100]));
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
        public async Task PreviewRename_ConfiguredRootsExist_StaleOutputPathDoesNotWidenCustomBase()
        {
            var managedRoot = Path.Join(_tempRoot, "managed-library");
            var legacyOutput = Path.Join(_tempRoot, "legacy-library");
            var customBase = Path.Join(legacyOutput, "Legacy Book");
            Directory.CreateDirectory(managedRoot);
            Directory.CreateDirectory(customBase);
            var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
            rootFolders.Setup(service => service.GetAllAsync())
                .ReturnsAsync([
                    new RootFolder
                    {
                        Id = 1,
                        Name = "Managed Library",
                        Path = managedRoot,
                        IsDefault = true,
                        CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                        ResolvedCaseSensitivity =
                            FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
                        PathIdentityState = PathIdentityState.Valid
                    }
                ]);
            var settings = new ApplicationSettings
            {
                OutputPath = legacyOutput,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };
            var (service, db, _) = BuildService(
                settings,
                rootFolderServiceOverride: rootFolders.Object);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 3,
                Title = "Dune",
                Authors = ["Frank Herbert"],
                BasePath = customBase,
                Files =
                [
                    new AudiobookFile
                    {
                        Id = 31,
                        AudiobookId = 3,
                        Path = Path.Join(customBase, "wrong-name.m4b"),
                        Format = "m4b"
                    }
                ]
            });
            await db.SaveChangesAsync();

            var preview = Assert.Single(await service.PreviewRenameAsync([3]));

            Assert.False(preview.FolderChanged);
            Assert.Equal(NormalizePath(customBase), preview.NewFolderPath);
            Assert.All(preview.FileRenames, file =>
                Assert.StartsWith(
                    NormalizePath(customBase),
                    file.NewPath!,
                    StringComparison.OrdinalIgnoreCase));
            rootFolders.VerifyAll();
        }

        [Fact]
        public async Task PreviewRename_RootFolderServiceUnavailable_DoesNotFallBackToLegacyOutputPath()
        {
            var legacyOutput = Path.Join(_tempRoot, "legacy-organize-root");
            var currentBase = Path.Join(legacyOutput, "Dune");
            Directory.CreateDirectory(currentBase);
            var rootFolders = new Mock<IRootFolderService>(MockBehavior.Strict);
            rootFolders.Setup(service => service.GetAllAsync())
                .ThrowsAsync(new InvalidOperationException("Injected root folder outage."));
            var (service, db, _) = BuildService(
                new ApplicationSettings
                {
                    OutputPath = legacyOutput,
                    FolderNamingPattern = "{Author}/{Title}",
                    FileNamingPattern = "{Title}"
                },
                rootFolderServiceOverride: rootFolders.Object);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 4,
                Title = "Dune",
                Authors = ["Frank Herbert"],
                BasePath = currentBase,
                Files =
                [
                    new AudiobookFile
                    {
                        Id = 41,
                        AudiobookId = 4,
                        Path = Path.Join(currentBase, "wrong-name.m4b"),
                        Format = "m4b"
                    }
                ]
            });
            await db.SaveChangesAsync();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PreviewRenameAsync([4]));

            Assert.Contains(
                "root folder",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            rootFolders.VerifyAll();
        }

        [Fact]
        public async Task ExecuteRename_CustomBasePathOutsideConfiguredRoots_AllowsInPlaceRename()
        {
            var outputPath = Path.Join(_tempRoot, "configured-library");
            var customBase = Path.Join(_tempRoot, "custom-shelf", "Dune");
            var sourcePath = Path.Join(customBase, "wrong-name.m4b");
            var targetPath = Path.Join(customBase, "Dune.m4b");
            Directory.CreateDirectory(customBase);
            await File.WriteAllTextAsync(sourcePath, "audio");

            var (service, db, _) = BuildService(new ApplicationSettings
            {
                OutputPath = outputPath,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            });
            var audiobook = new Audiobook
            {
                Id = 20,
                Title = "Dune",
                Authors = ["Frank Herbert"],
                BasePath = customBase,
                Files =
                [
                    CreateTrackedFile(201, 20, sourcePath)
                ]
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 20,
                    CurrentFolderSemantics = ExpectedSemantics(customBase),
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 201,
                            CurrentPath = sourcePath,
                            NewPath = targetPath
                        }
                    ]
                }
            ]));

            Assert.True(result.Success);
            Assert.Equal(NormalizePath(customBase), NormalizePath(audiobook.BasePath));
            Assert.True(File.Exists(targetPath));
            Assert.False(File.Exists(sourcePath));
        }

        [WindowsFact]
        public async Task ExecuteRename_ForeignUnixBasePath_DoesNotAuthorizeWindowsAlias()
        {
            var nativeBase = Path.Join(_tempRoot, "cross-host-organize");
            var nativeSource = Path.Join(nativeBase, "wrong-name.m4b");
            var nativeTarget = Path.Join(nativeBase, "Dune.m4b");
            Directory.CreateDirectory(nativeBase);
            await File.WriteAllTextAsync(nativeSource, "audio");

            var foreignBase = WindowsPathTestFixture
                .GetRootRelativeForeignAlias(nativeBase);

            var configuredOutput = Path.Join(_tempRoot, "configured-library");
            var (service, db, _) = BuildService(new ApplicationSettings
            {
                OutputPath = configuredOutput,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            });
            db.Audiobooks.Add(new Audiobook
            {
                Id = 21,
                Title = "Dune",
                Authors = ["Frank Herbert"],
                BasePath = foreignBase,
                Files =
                [
                    new AudiobookFile
                    {
                        Id = 211,
                        AudiobookId = 21,
                        Path = nativeSource,
                        Format = "m4b"
                    }
                ]
            });
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 21,
                    CurrentFolderSemantics = ExpectedSemantics(nativeBase),
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 211,
                            CurrentPath = nativeSource,
                            NewPath = nativeTarget
                        }
                    ]
                }
            ]));

            Assert.False(result.Success);
            Assert.True(File.Exists(nativeSource));
            Assert.False(File.Exists(nativeTarget));
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
                    CurrentFolderSemantics = ExpectedSemantics(bookFolder),
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
                    CurrentFolderSemantics = ExpectedSemantics(bookFolder),
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
                    CurrentFolderSemantics = ExpectedSemantics(bookFolder),
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
        public async Task ExecuteRename_DestinationOwnedByDifferentFileRow_IsRejectedBeforeMove()
        {
            var libraryRoot = Path.Join(_tempRoot, "owned-destination");
            var bookFolder = Path.Join(libraryRoot, "Book");
            Directory.CreateDirectory(bookFolder);
            var sourcePath = Path.Join(bookFolder, "source.m4b");
            var targetPath = Path.Join(bookFolder, "target.m4b");
            await File.WriteAllTextAsync(sourcePath, "audio");

            var targetFullPath = Path.GetFullPath(targetPath);
            var targetIdentity = AudiobookFilePathIdentity.CreateValid(
                targetFullPath,
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemCaseSensitivityMode.Auto,
                Path.GetPathRoot(targetFullPath)!);
            var targetOwner = AudiobookFile.CreateUnresolved(targetPath);
            targetOwner.Id = 142;
            targetOwner.AudiobookId = 14;
            targetOwner.ApplyPathIdentity(targetPath, targetIdentity);

            var (service, db, _) = BuildService(new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            });
            db.Audiobooks.Add(new Audiobook
            {
                Id = 14,
                Title = "Book",
                BasePath = bookFolder,
                Files =
                [
                    new AudiobookFile
                    {
                        Id = 141,
                        AudiobookId = 14,
                        Path = sourcePath
                    },
                    targetOwner
                ]
            });
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 14,
                    CurrentFolderSemantics = ExpectedSemantics(bookFolder),
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 141,
                            CurrentPath = sourcePath,
                            NewPath = targetPath
                        }
                    ]
                }
            ]));

            Assert.False(result.Success);
            var fileResult = Assert.Single(result.RenamedFiles);
            Assert.Contains("already owned", fileResult.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(targetPath));
        }

        [Fact]
        public async Task ExecuteRename_TargetUnderDifferentConfiguredRoot_IsRejected()
        {
            var rootA = Path.Join(_tempRoot, "organize-root-a");
            var rootB = Path.Join(_tempRoot, "organize-root-b");
            var sourceFolder = Path.Join(rootA, "Author", "Book");
            var targetFolder = Path.Join(rootB, "Author", "Book");
            var sourceFile = Path.Join(sourceFolder, "Book.m4b");
            var targetFile = Path.Join(targetFolder, "Book.m4b");
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(rootB);
            await File.WriteAllTextAsync(sourceFile, "audio");

            var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
            rootFolderService.Setup(service => service.GetAllAsync())
                .ReturnsAsync(
                [
                    new RootFolder
                    {
                        Id = 1,
                        Name = "Root A",
                        Path = rootA,
                        IsDefault = true
                    },
                    new RootFolder
                    {
                        Id = 2,
                        Name = "Root B",
                        Path = rootB
                    }
                ]);
            var (service, db, _) = BuildService(
                new ApplicationSettings
                {
                    OutputPath = rootA,
                    FolderNamingPattern = "{Author}/{Title}",
                    FileNamingPattern = "{Title}"
                },
                rootFolderServiceOverride: rootFolderService.Object);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 13,
                Title = "Book",
                Authors = ["Author"],
                BasePath = sourceFolder,
                Files = [CreateTrackedFile(131, 13, sourceFile)]
            });
            await db.SaveChangesAsync();

            var preview = Assert.Single(await service.PreviewRenameAsync([13]));
            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 13,
                    CurrentFolderPath = preview.CurrentFolderPath,
                    CurrentFolderSemantics = preview.CurrentFolderSemantics,
                    NewFolderPath = targetFolder,
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 131,
                            CurrentPath = sourceFile,
                            NewPath = targetFile
                        }
                    ]
                }
            ]));

            Assert.False(result.Success);
            Assert.Contains("outside", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(sourceFolder));
            Assert.False(Directory.Exists(targetFolder));
        }

        [DirectoryLinkFact]
        public async Task ExecuteRename_SymbolicLinkDestinationOutsideRoot_IsRejected()
        {

            var libraryRoot = Path.Join(_tempRoot, "library-link-guard");
            var sourceFolder = Path.Join(libraryRoot, "Old");
            var outsideRoot = Path.Join(_tempRoot, "outside-link-target");
            var linkedRoot = Path.Join(libraryRoot, "linked");
            var targetFolder = Path.Join(linkedRoot, "Book");
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(outsideRoot);
            Directory.CreateSymbolicLink(linkedRoot, outsideRoot);
            var sourceFile = Path.Join(sourceFolder, "book.m4b");
            var targetFile = Path.Join(targetFolder, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "audio");
            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };
            var (service, db, _) = BuildService(settings);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 70,
                Title = "Book",
                Authors = new List<string> { "Author" },
                BasePath = sourceFolder,
                Files = [CreateTrackedFile(701, 70, sourceFile)]
            });
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(new List<RenameOperation>
            {
                new()
                {
                    AudiobookId = 70,
                    CurrentFolderPath = sourceFolder,
                    CurrentFolderSemantics = ExpectedSemantics(sourceFolder),
                    NewFolderPath = targetFolder,
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 701,
                            CurrentPath = sourceFile,
                            NewPath = targetFile
                        }
                    ]
                }
            }));

            Assert.False(result.Success);
            Assert.Contains("resolved safely", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(sourceFolder));
            Assert.False(Directory.Exists(Path.Join(outsideRoot, "Book")));
        }

        [Fact]
        public async Task ExecuteRename_RollsBackCompletedFileMovesAfterFailure()
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
            var forwardOperationIds = new List<Guid>();

            var (service, db, dbName) = BuildService(settings, fileMover =>
            {
                fileMover.Setup(mover => mover.MoveFilePreservingPhysicalIdentityAsync(
                        It.Is<string>(source => PathsEqualForTest(source, firstSourcePath)),
                        It.Is<string>(dest => PathsEqualForTest(dest, firstTargetPath)),
                        It.IsAny<string>(),
                        It.IsAny<Guid>(),
                        7,
                        71))
                    .Returns<string, string, string, Guid, int, int>(
                        (source, dest, expectedIdentity, operationId, _, _) =>
                        {
                            Assert.Equal(expectedIdentity, GetPhysicalObjectIdentity(source));
                            forwardOperationIds.Add(operationId);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            File.Move(source, dest, overwrite: true);
                            return Task.FromResult(true);
                        });
                fileMover.Setup(mover => mover.MoveFilePreservingPhysicalIdentityAsync(
                    It.IsAny<string>(),
                    It.Is<string>(dest => dest.EndsWith("Part 2.m4b", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<int>()))
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
                    CreateTrackedFile(71, 7, firstSourcePath),
                    CreateTrackedFile(72, 7, secondSourcePath)
                }
            });
            await db.SaveChangesAsync();

            var operations = new List<RenameOperation>
            {
                new()
                {
                    AudiobookId = 7,
                    CurrentFolderPath = sourceFolder,
                    CurrentFolderSemantics = ExpectedSemantics(sourceFolder),
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
            };
            var results = await service.ExecuteRenameAsync(operations);

            var result = Assert.Single(results);
            Assert.False(result.Success);
            Assert.Equal(2, result.RenamedFiles.Count);
            Assert.Contains(result.RenamedFiles, item => item.FileId == 71 && item.RolledBack && !item.Success);
            Assert.Contains(result.RenamedFiles, item => item.FileId == 72 && !item.Success && !item.RolledBack);

            await using var verifyDb = CreateContext(dbName);
            var saved = await verifyDb.Audiobooks.Include(a => a.Files).SingleAsync(a => a.Id == 7);

            Assert.Equal(NormalizePath(sourceFolder), NormalizePath(saved.BasePath));
            Assert.Contains(saved.Files!, file => file.Id == 71 && NormalizePath(file.Path) == NormalizePath(firstSourcePath));
            Assert.Contains(saved.Files!, file => file.Id == 72 && NormalizePath(file.Path) == NormalizePath(secondSourcePath));
            Assert.True(File.Exists(firstSourcePath));
            Assert.True(File.Exists(secondSourcePath));
            Assert.False(File.Exists(firstTargetPath));
            Assert.False(File.Exists(secondTargetPath));

            var retry = Assert.Single(await service.ExecuteRenameAsync(operations));
            Assert.False(retry.Success);
            Assert.True(File.Exists(firstSourcePath));
            Assert.True(File.Exists(secondSourcePath));
            Assert.False(File.Exists(firstTargetPath));
            Assert.False(File.Exists(secondTargetPath));
            Assert.Equal(2, forwardOperationIds.Count);
            Assert.NotEqual(forwardOperationIds[0], forwardOperationIds[1]);
        }

        [Fact]
        public async Task ExecuteRename_MoverException_DoesNotExposeInternalError()
        {
            var libraryRoot = Path.Join(_tempRoot, "rename-public-error");
            var sourceFolder = Path.Join(libraryRoot, "Book");
            Directory.CreateDirectory(sourceFolder);
            var sourcePath = Path.Join(sourceFolder, "Old.m4b");
            var targetPath = Path.Join(sourceFolder, "New.m4b");
            await File.WriteAllTextAsync(sourcePath, "audio");
            const string secret = "C:\\private\\organize-secret";

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Title}",
                FileNamingPattern = "{Title}"
            };
            var (service, db, _) = BuildService(settings, fileMover =>
            {
                fileMover.Setup(mover => mover.MoveFilePreservingPhysicalIdentityAsync(
                        sourcePath,
                        targetPath,
                        It.IsAny<string>(),
                        It.IsAny<Guid>(),
                        It.IsAny<int>(),
                        It.IsAny<int>()))
                    .ThrowsAsync(new IOException(secret));
            });
            db.Audiobooks.Add(new Audiobook
            {
                Id = 73,
                Title = "Book",
                BasePath = sourceFolder,
                Files =
                [
                    CreateTrackedFile(731, 73, sourcePath)
                ]
            });
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 73,
                    CurrentFolderPath = sourceFolder,
                    CurrentFolderSemantics = ExpectedSemantics(sourceFolder),
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 731,
                            CurrentPath = sourcePath,
                            NewPath = targetPath
                        }
                    ]
                }
            ]));

            Assert.False(result.Success);
            Assert.Equal("File organize operation failed.", result.Error);
            Assert.Equal(
                "File organize operation failed.",
                Assert.Single(result.RenamedFiles).Error);
            Assert.DoesNotContain(secret, result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(targetPath));
        }

        [Fact]
        public async Task ExecuteRename_UnresolvedMoveExecution_BlocksBeforeFileMutation()
        {
            var libraryRoot = Path.Join(_tempRoot, "unresolved-move-rename");
            var sourceFolder = Path.Join(libraryRoot, "Old");
            var targetFolder = Path.Join(libraryRoot, "Author", "Book");
            Directory.CreateDirectory(sourceFolder);
            var sourcePath = Path.Join(sourceFolder, "old-name.m4b");
            var targetPath = Path.Join(targetFolder, "Book.m4b");
            await File.WriteAllTextAsync(sourcePath, "test");
            var moveQueue = new Mock<IMoveQueueService>(MockBehavior.Strict);
            moveQueue.Setup(service => service.EnsureFilesystemMutationAllowedAsync(
                    44,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ApplicationConflictException(
                    "move_recovery_required",
                    "An interrupted move still owns this audiobook's filesystem state."));
            var (service, db, _) = BuildService(
                new ApplicationSettings
                {
                    OutputPath = libraryRoot,
                    FolderNamingPattern = "{Author}/{Title}",
                    FileNamingPattern = "{Title}"
                },
                moveQueueServiceOverride: moveQueue.Object);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 44,
                Title = "Book",
                Authors = ["Author"],
                BasePath = sourceFolder,
                FilePath = sourcePath,
                Files = [CreateTrackedFile(441, 44, sourcePath)]
            });
            await db.SaveChangesAsync();

            var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
                service.ExecuteRenameAsync(
                [
                    new RenameOperation
                    {
                        AudiobookId = 44,
                        CurrentFolderPath = sourceFolder,
                        CurrentFolderSemantics = ExpectedSemantics(sourceFolder),
                        NewFolderPath = targetFolder,
                        FileRenames =
                        [
                            new FileRenameOperation
                            {
                                FileId = 441,
                                CurrentPath = sourcePath,
                                NewPath = targetPath
                            }
                        ]
                    }
                ]));

            Assert.Equal("move_recovery_required", exception.Code);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(targetPath));
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
                    CreateTrackedFile(41, 4, sourcePath)
                }
            });
            await db.SaveChangesAsync();

            var results = await service.ExecuteRenameAsync(new List<RenameOperation>
            {
                new()
                {
                    AudiobookId = 4,
                    CurrentFolderPath = sourceFolder,
                    CurrentFolderSemantics = ExpectedSemantics(sourceFolder),
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
        public async Task PreviewRename_RelativeStoredFilePath_UsesAudiobookBasePath()
        {
            var libraryRoot = Path.Join(_tempRoot, "relative-preview");
            var bookFolder = Path.Join(libraryRoot, "Author", "Book");
            var relativePath = Path.Join("Disc 1", "old-name.m4b");
            var sourcePath = Path.Join(bookFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllTextAsync(sourcePath, "audio");

            var (service, db, _) = BuildService(new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            });
            db.Audiobooks.Add(new Audiobook
            {
                Id = 16,
                Title = "Book",
                Authors = ["Author"],
                BasePath = bookFolder,
                Files =
                [
                    new AudiobookFile
                    {
                        Id = 161,
                        AudiobookId = 16,
                        Path = relativePath,
                        Format = "m4b"
                    }
                ]
            });
            await db.SaveChangesAsync();

            var preview = Assert.Single(await service.PreviewRenameAsync([16]));
            var file = Assert.Single(preview.FileRenames);

            Assert.Equal(NormalizePath(sourcePath), NormalizePath(file.CurrentPath));
            Assert.StartsWith(NormalizePath(libraryRoot), NormalizePath(file.NewPath), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExecuteRename_FolderChangeMissingTrackedFile_IsRejectedBeforeMutation()
        {
            var libraryRoot = Path.Join(_tempRoot, "incomplete-folder-plan");
            var sourceFolder = Path.Join(libraryRoot, "Old");
            var targetFolder = Path.Join(libraryRoot, "New");
            var firstSource = Path.Join(sourceFolder, "first.m4b");
            var secondSource = Path.Join(sourceFolder, "second.m4b");
            var firstTarget = Path.Join(targetFolder, "first.m4b");
            Directory.CreateDirectory(sourceFolder);
            await File.WriteAllTextAsync(firstSource, "first");
            await File.WriteAllTextAsync(secondSource, "second");

            var (service, db, _) = BuildService(new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            });
            db.Audiobooks.Add(new Audiobook
            {
                Id = 17,
                Title = "Book",
                BasePath = sourceFolder,
                Files =
                [
                    new AudiobookFile { Id = 171, AudiobookId = 17, Path = firstSource },
                    new AudiobookFile { Id = 172, AudiobookId = 17, Path = secondSource }
                ]
            });
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 17,
                    CurrentFolderPath = sourceFolder,
                    CurrentFolderSemantics = ExpectedSemantics(sourceFolder),
                    NewFolderPath = targetFolder,
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 171,
                            CurrentPath = firstSource,
                            NewPath = firstTarget
                        }
                    ]
                }
            ]));

            Assert.False(result.Success);
            Assert.True(result.Conflict);
            Assert.Contains("every tracked", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(firstSource));
            Assert.True(File.Exists(secondSource));
            Assert.False(Directory.Exists(targetFolder));
        }

        [Fact]
        public async Task ExecuteRename_PartialInFolderRename_WithRelativeUnchangedRow_PreservesBasePath()
        {
            var libraryRoot = Path.Join(_tempRoot, "partial-relative-summary");
            var bookFolder = Path.Join(libraryRoot, "Book");
            var firstRelative = "first-old.m4b";
            var secondRelative = "second.m4b";
            var firstSource = Path.Join(bookFolder, firstRelative);
            var secondSource = Path.Join(bookFolder, secondRelative);
            var firstTarget = Path.Join(bookFolder, "first-new.m4b");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(firstSource, "first");
            await File.WriteAllTextAsync(secondSource, "second");

            var (service, db, _) = BuildService(new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            });
            var audiobook = new Audiobook
            {
                Id = 18,
                Title = "Book",
                BasePath = bookFolder,
                Files =
                [
                    CreateTrackedFile(181, 18, firstRelative, firstSource),
                    CreateTrackedFile(182, 18, secondRelative, secondSource)
                ]
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 18,
                    CurrentFolderSemantics = ExpectedSemantics(bookFolder),
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 181,
                            CurrentPath = firstSource,
                            NewPath = firstTarget
                        }
                    ]
                }
            ]));

            Assert.True(result.Success);
            Assert.Equal(NormalizePath(bookFolder), NormalizePath(audiobook.BasePath));
            Assert.StartsWith(NormalizePath(bookFolder), NormalizePath(audiobook.FilePath), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(firstTarget));
            Assert.True(File.Exists(secondSource));
        }

        [Fact]
        public async Task ExecuteRename_RelativeStoredFilePath_ResolvesAgainstAudiobookBasePath()
        {
            var libraryRoot = Path.Join(_tempRoot, "relative-file-rename");
            var sourceFolder = Path.Join(libraryRoot, "Author", "Book");
            var targetFolder = Path.Join(libraryRoot, "Author", "Renamed Book");
            var relativePath = Path.Join("Disc 1", "Book.m4b");
            var sourcePath = Path.Join(sourceFolder, relativePath);
            var targetPath = Path.Join(targetFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllTextAsync(sourcePath, "audio");

            var (service, db, _) = BuildService(new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            });
            var audiobook = new Audiobook
            {
                Id = 11,
                Title = "Book",
                Authors = ["Author"],
                BasePath = sourceFolder,
                FilePath = sourcePath,
                Files =
                [
                    CreateTrackedFile(111, 11, relativePath, sourcePath)
                ]
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 11,
                    CurrentFolderPath = sourceFolder,
                    CurrentFolderSemantics = ExpectedSemantics(sourceFolder),
                    NewFolderPath = targetFolder,
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 111,
                            CurrentPath = sourcePath,
                            NewPath = targetPath
                        }
                    ]
                }
            ]));

            Assert.True(result.Success);
            Assert.False(File.Exists(sourcePath));
            Assert.True(File.Exists(targetPath));
            Assert.Equal(NormalizePath(targetPath), NormalizePath(audiobook.Files!.Single().Path));
            Assert.Equal(PathIdentityState.Valid, audiobook.Files.Single().PathIdentityState);
        }

        [Fact]
        public async Task ExecuteRename_CancellationAfterFirstFileMove_CompletesStableCommit()
        {
            var libraryRoot = Path.Join(_tempRoot, "cancel-after-mutation");
            var bookFolder = Path.Join(libraryRoot, "Book");
            Directory.CreateDirectory(bookFolder);
            var firstSource = Path.Join(bookFolder, "first-old.m4b");
            var secondSource = Path.Join(bookFolder, "second-old.m4b");
            var firstTarget = Path.Join(bookFolder, "first-new.m4b");
            var secondTarget = Path.Join(bookFolder, "second-new.m4b");
            await File.WriteAllTextAsync(firstSource, "first");
            await File.WriteAllTextAsync(secondSource, "second");
            using var cancellation = new CancellationTokenSource();

            var identityResolver = new Mock<IAudiobookFilePathIdentityResolver>(MockBehavior.Strict);
            identityResolver.Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns<Audiobook, string, CancellationToken>((_, path, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var fullPath = Path.GetFullPath(path);
                    return ValueTask.FromResult(AudiobookFilePathIdentity.CreateValid(
                        fullPath,
                        FileSystemPathSemantics.CurrentHostDefault,
                        FileSystemCaseSensitivityMode.Auto,
                        Path.GetPathRoot(fullPath)!));
                });
            var (service, db, _) = BuildService(
                new ApplicationSettings
                {
                    OutputPath = libraryRoot,
                    FolderNamingPattern = "{Author}/{Title}",
                    FileNamingPattern = "{Title}"
                },
                fileMover => fileMover.Setup(mover => mover.MoveFilePreservingPhysicalIdentityAsync(
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<Guid>(),
                        It.IsAny<int>(),
                        It.IsAny<int>()))
                    .Returns<string, string, string, Guid, int, int>((source, destination, _, _, _, _) =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Move(source, destination, overwrite: true);
                        if (PathsEqualForTest(destination, firstTarget))
                        {
                            cancellation.Cancel();
                        }

                        return Task.FromResult(true);
                    }),
                identityResolverOverride: identityResolver.Object);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 15,
                Title = "Book",
                BasePath = bookFolder,
                Files =
                [
                    CreateTrackedFile(151, 15, firstSource),
                    CreateTrackedFile(152, 15, secondSource)
                ]
            });
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 15,
                    CurrentFolderSemantics = ExpectedSemantics(bookFolder),
                    FileRenames =
                    [
                        new FileRenameOperation
                        {
                            FileId = 151,
                            CurrentPath = firstSource,
                            NewPath = firstTarget
                        },
                        new FileRenameOperation
                        {
                            FileId = 152,
                            CurrentPath = secondSource,
                            NewPath = secondTarget
                        }
                    ]
                }
            ], cancellation.Token));

            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(result.Success);
            Assert.True(File.Exists(firstTarget));
            Assert.True(File.Exists(secondTarget));
            Assert.False(File.Exists(firstSource));
            Assert.False(File.Exists(secondSource));
        }

        [Fact]
        public async Task ExecuteRename_FilesystemSemanticsChangedAfterPreview_RejectsBeforeMutation()
        {
            var libraryRoot = Path.Join(_tempRoot, "semantics-change");
            var sourceFolder = Path.Join(libraryRoot, "Author", "Book");
            var targetFolder = Path.Join(libraryRoot, "Author", "Renamed Book");
            Directory.CreateDirectory(sourceFolder);

            var currentSensitivity = FileSystemCaseSensitivity.Sensitive;
            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
            semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(
                            FileSystemPathSemantics.CurrentHostDefault.Syntax,
                            currentSensitivity),
                        PathIdentityState.Valid,
                        path)));

            var (service, db, _) = BuildService(
                new ApplicationSettings
                {
                    OutputPath = libraryRoot,
                    FolderNamingPattern = "{Author}/{Title}",
                    FileNamingPattern = "{Title}"
                },
                semanticsResolverOverride: semanticsResolver.Object);
            db.Audiobooks.Add(new Audiobook
            {
                Id = 12,
                Title = "Book",
                Authors = ["Author"],
                BasePath = sourceFolder
            });
            await db.SaveChangesAsync();

            var preview = Assert.Single(await service.PreviewRenameAsync([12]));
            Assert.NotNull(preview.CurrentFolderSemantics);
            Assert.Equal(
                FileSystemCaseSensitivity.Sensitive,
                preview.CurrentFolderSemantics!.CaseSensitivity);

            currentSensitivity = FileSystemCaseSensitivity.Insensitive;
            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 12,
                    CurrentFolderPath = preview.CurrentFolderPath,
                    CurrentFolderSemantics = preview.CurrentFolderSemantics,
                    NewFolderPath = targetFolder
                }
            ]));

            Assert.False(result.Success);
            Assert.True(result.Conflict);
            Assert.Contains("semantics changed", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(sourceFolder));
            Assert.False(Directory.Exists(targetFolder));
        }

        [Fact]
        public async Task ExecuteRename_AcquiresGlobalMutationBoundaryBeforeAudiobookLocks()
        {
            var events = new List<string>();
            var globalCoordinator = new RecordingFilesystemMutationCoordinator(events);
            var audiobookCoordinator = new RecordingAudiobookOperationCoordinator(events);
            var (service, _, _) = BuildService(
                new ApplicationSettings { OutputPath = _tempRoot },
                mutationCoordinator: globalCoordinator,
                operationCoordinator: audiobookCoordinator);

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation { AudiobookId = 404 }
            ]));

            Assert.False(result.Success);
            Assert.Equal(
                ["global-enter", "audiobook-enter", "audiobook-exit", "global-exit"],
                events);
        }

        [Fact]
        public async Task ExecuteRename_RelocationWhileWaitingForGlobalLock_RejectsStalePreview()
        {
            var rootA = Path.Join(_tempRoot, "stale-root-a");
            var rootB = Path.Join(_tempRoot, "stale-root-b");
            var sourceA = Path.Join(rootA, "Author", "Book");
            var sourceB = Path.Join(rootB, "Author", "Book");
            var targetA = Path.Join(rootA, "Author", "Renamed Book");
            var fileA = Path.Join(sourceA, "Book.m4b");
            var fileB = Path.Join(sourceB, "Book.m4b");
            Directory.CreateDirectory(sourceA);
            await File.WriteAllTextAsync(fileA, "audio");

            var settings = new ApplicationSettings
            {
                OutputPath = rootA,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };
            var coordinator = new PausingFilesystemMutationCoordinator();
            var (service, db, _) = BuildService(
                settings,
                mutationCoordinator: coordinator);
            var audiobook = new Audiobook
            {
                Id = 9,
                Title = "Book",
                Authors = ["Author"],
                BasePath = sourceA,
                FilePath = fileA,
                Files =
                [
                    new AudiobookFile
                    {
                        Id = 91,
                        AudiobookId = 9,
                        Path = fileA,
                        Format = "m4b"
                    }
                ]
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();

            var renameTask = service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 9,
                    CurrentFolderPath = sourceA,
                    CurrentFolderSemantics = ExpectedSemantics(sourceA),
                    NewFolderPath = targetA
                }
            ]);
            await coordinator.WaitUntilEnteredAsync();

            Directory.CreateDirectory(Path.GetDirectoryName(sourceB)!);
            Directory.Move(sourceA, sourceB);
            audiobook.BasePath = sourceB;
            audiobook.FilePath = fileB;
            audiobook.Files!.Single().Path = fileB;
            settings.OutputPath = rootB;
            await db.SaveChangesAsync();
            coordinator.Release();

            var result = Assert.Single(await renameTask);
            Assert.False(result.Success);
            Assert.True(result.Conflict);
            Assert.Contains("changed", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(fileB));
            Assert.False(Directory.Exists(targetA));
            Assert.False(Directory.Exists(sourceA));
        }

        [Fact]
        public async Task ExecuteRename_FilePersistenceFailure_RestoresSameScopeStateForRetry()
        {
            var libraryRoot = Path.Join(_tempRoot, "file-persistence-rollback");
            var sourceFolder = Path.Join(libraryRoot, "Old");
            var targetFolder = Path.Join(libraryRoot, "Author", "Book");
            var sourcePath = Path.Join(sourceFolder, "old-name.m4b");
            var targetPath = Path.Join(targetFolder, "Book.m4b");
            Directory.CreateDirectory(sourceFolder);
            await File.WriteAllTextAsync(sourcePath, "audio");

            var settings = new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            };
            var (service, db, _) = BuildService(
                settings,
                contextFactory: options => new FailureInjectingListenArrDbContext(options));
            var failureContext = Assert.IsType<FailureInjectingListenArrDbContext>(db);
            var audiobook = new Audiobook
            {
                Id = 10,
                Title = "Book",
                Authors = ["Author"],
                BasePath = sourceFolder,
                FilePath = sourcePath,
                FileSize = 5,
                Files =
                [
                    CreateTrackedFile(101, 10, sourcePath, size: 5)
                ]
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var operation = new RenameOperation
            {
                AudiobookId = 10,
                CurrentFolderPath = sourceFolder,
                CurrentFolderSemantics = ExpectedSemantics(sourceFolder),
                NewFolderPath = targetFolder,
                FileRenames =
                [
                    new FileRenameOperation
                    {
                        FileId = 101,
                        CurrentPath = sourcePath,
                        NewPath = targetPath
                    }
                ]
            };
            failureContext.FailNextSave = true;

            var failed = Assert.Single(await service.ExecuteRenameAsync([operation]));

            Assert.False(failed.Success);
            Assert.Contains("rolled back", failed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(NormalizePath(sourceFolder), NormalizePath(audiobook.BasePath));
            Assert.Equal(NormalizePath(sourcePath), NormalizePath(audiobook.FilePath));
            Assert.Equal(5, audiobook.FileSize);
            Assert.Equal(NormalizePath(sourcePath), NormalizePath(audiobook.Files!.Single().Path));
            Assert.Equal(PathIdentityState.Unavailable, audiobook.Files.Single().PathIdentityState);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(targetPath));

            var retried = Assert.Single(await service.ExecuteRenameAsync([operation]));

            Assert.True(retried.Success);
            Assert.Equal(NormalizePath(targetFolder), NormalizePath(audiobook.BasePath));
            Assert.Equal(NormalizePath(targetPath), NormalizePath(audiobook.FilePath));
            Assert.Equal(NormalizePath(targetPath), NormalizePath(audiobook.Files.Single().Path));
            Assert.Equal(PathIdentityState.Valid, audiobook.Files.Single().PathIdentityState);
            Assert.False(File.Exists(sourcePath));
            Assert.True(File.Exists(targetPath));
        }

        [Fact]
        public async Task ExecuteRename_FolderOnlyRequest_DoesNotMoveUnownedDirectoryTree()
        {
            var libraryRoot = Path.Join(_tempRoot, "folder-only-rejected");
            var sourceFolder = Path.Join(libraryRoot, "Old");
            var targetFolder = Path.Join(libraryRoot, "Author", "Book");
            var sourcePath = Path.Join(sourceFolder, "Book.m4b");
            var unrelatedPath = Path.Join(sourceFolder, "foreign.txt");
            Directory.CreateDirectory(sourceFolder);
            await File.WriteAllTextAsync(sourcePath, "audio");
            await File.WriteAllTextAsync(unrelatedPath, "foreign");

            var (service, db, _) = BuildService(new ApplicationSettings
            {
                OutputPath = libraryRoot,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}"
            });
            var audiobook = new Audiobook
            {
                Id = 8,
                Title = "Book",
                Authors = ["Author"],
                BasePath = sourceFolder,
                FilePath = sourcePath,
                Files = [CreateTrackedFile(81, 8, sourcePath)]
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();

            var result = Assert.Single(await service.ExecuteRenameAsync(
            [
                new RenameOperation
                {
                    AudiobookId = 8,
                    CurrentFolderPath = sourceFolder,
                    CurrentFolderSemantics = ExpectedSemantics(sourceFolder),
                    NewFolderPath = targetFolder
                }
            ]));

            Assert.False(result.Success);
            Assert.True(result.Conflict);
            Assert.Contains("every tracked", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(NormalizePath(sourceFolder), NormalizePath(audiobook.BasePath));
            Assert.True(File.Exists(sourcePath));
            Assert.Equal("foreign", await File.ReadAllTextAsync(unrelatedPath));
            Assert.False(Directory.Exists(targetFolder));
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

        private sealed class RecordingFilesystemMutationCoordinator(
            List<string> events) : IFilesystemMutationCoordinator
        {
            public async Task ExecuteExclusiveAsync(
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken = default)
            {
                events.Add("global-enter");
                await operation(cancellationToken);
                events.Add("global-exit");
            }

            public async Task<T> ExecuteExclusiveAsync<T>(
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken = default)
            {
                events.Add("global-enter");
                var result = await operation(cancellationToken);
                events.Add("global-exit");
                return result;
            }
        }

        private sealed class RecordingAudiobookOperationCoordinator(
            List<string> events) : IAudiobookOperationCoordinator
        {
            public Task ExecuteExclusiveAsync(
                int audiobookId,
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken = default) =>
                ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

            public Task<T> ExecuteExclusiveAsync<T>(
                int audiobookId,
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken = default) =>
                ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

            public async Task ExecuteExclusiveAsync(
                IEnumerable<int> audiobookIds,
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken = default)
            {
                events.Add("audiobook-enter");
                await operation(cancellationToken);
                events.Add("audiobook-exit");
            }

            public async Task<T> ExecuteExclusiveAsync<T>(
                IEnumerable<int> audiobookIds,
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken = default)
            {
                events.Add("audiobook-enter");
                var result = await operation(cancellationToken);
                events.Add("audiobook-exit");
                return result;
            }
        }

        private sealed class PausingFilesystemMutationCoordinator : IFilesystemMutationCoordinator
        {
            private readonly TaskCompletionSource<bool> _entered = new();
            private readonly TaskCompletionSource<bool> _release = new();

            public Task WaitUntilEnteredAsync() => _entered.Task;
            public void Release() => _release.TrySetResult(true);

            public async Task ExecuteExclusiveAsync(
                Func<CancellationToken, Task> operation,
                CancellationToken cancellationToken = default)
            {
                _entered.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
                await operation(cancellationToken);
            }

            public async Task<T> ExecuteExclusiveAsync<T>(
                Func<CancellationToken, Task<T>> operation,
                CancellationToken cancellationToken = default)
            {
                _entered.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
                return await operation(cancellationToken);
            }
        }

        private sealed class FailureInjectingListenArrDbContext(
            DbContextOptions<ListenArrDbContext> options) : ListenArrDbContext(options)
        {
            public bool FailNextSave { get; set; }

            public override Task<int> SaveChangesAsync(
                CancellationToken cancellationToken = default)
            {
                if (FailNextSave)
                {
                    FailNextSave = false;
                    throw new InvalidOperationException("Injected organize persistence failure.");
                }

                return base.SaveChangesAsync(cancellationToken);
            }
        }

        private (RenameService Service, ListenArrDbContext Db, string DbName) BuildService(
            ApplicationSettings settings,
            Action<Mock<IFileMover>>? configureFileMover = null,
            FileSystemCaseSensitivity? caseSensitivity = null,
            Func<DbContextOptions<ListenArrDbContext>, ListenArrDbContext>? contextFactory = null,
            IFilesystemMutationCoordinator? mutationCoordinator = null,
            IAudiobookOperationCoordinator? operationCoordinator = null,
            IFileSystemSemanticsResolver? semanticsResolverOverride = null,
            IRootFolderService? rootFolderServiceOverride = null,
            IAudiobookFilePathIdentityResolver? identityResolverOverride = null,
            IMoveQueueService? moveQueueServiceOverride = null)
        {
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(dbName)
                .Options;
            var db = contextFactory?.Invoke(options) ?? new ListenArrDbContext(options);
            _contexts.Add(db);

            var config = new Mock<IConfigurationService>();
            config.Setup(service => service.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            var repo = new AudiobookRepository(db);
            var fileNaming = new FileNamingService(config.Object, NullLogger<FileNamingService>.Instance);
            var fileMover = new Mock<IFileMover>();
            fileMover.Setup(mover => mover.PerformActionOn(
                    FileAction.Move,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<FileAction, string, string, Guid>((action, source, dest, _) =>
                {
                    var dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.Move(source, dest, true);
                    return Task.FromResult(true);
                });
            fileMover.Setup(mover => mover.MoveFilePreservingPhysicalIdentityAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()))
                .Returns<string, string, string, Guid>((source, dest, expectedIdentity, _) =>
                    MovePreservingIdentity(source, dest, expectedIdentity));
            fileMover.Setup(mover => mover.MoveFilePreservingPhysicalIdentityAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<int>()))
                .Returns<string, string, string, Guid, int, int>(
                    (source, dest, expectedIdentity, _, _, _) =>
                        MovePreservingIdentity(source, dest, expectedIdentity));

            Task<bool> MovePreservingIdentity(
                string source,
                string dest,
                string expectedIdentity)
            {
                if (!string.Equals(
                        GetPhysicalObjectIdentity(source),
                        expectedIdentity,
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Move(source, dest, true);
                return Task.FromResult(string.Equals(
                    GetPhysicalObjectIdentity(dest),
                    expectedIdentity,
                    StringComparison.Ordinal));
            }
            fileMover.Setup(mover => mover.PerformActionOn(
                    FileAction.Move,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<int>(),
                    It.IsAny<int>()))
                .Returns<FileAction, string, string, Guid, int, int>(
                    (action, source, dest, _, _, _) =>
                    {
                        var dir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        File.Move(source, dest, true);
                        return Task.FromResult(true);
                    });
            configureFileMover?.Invoke(fileMover);
            var semanticsResolver = semanticsResolverOverride
                ?? BuildSemanticsResolver(caseSensitivity);
            var fileRepository = new EfAudiobookFileRepository(db);
            var identityResolver = identityResolverOverride;
            if (identityResolver == null)
            {
                var identityResolverMock = new Mock<IAudiobookFilePathIdentityResolver>();
                identityResolverMock.Setup(resolver => resolver.ResolveAsync(
                        It.IsAny<Audiobook>(),
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()))
                    .Returns<Audiobook, string, CancellationToken>((_, path, _) =>
                    {
                        var semantics = new FileSystemPathSemantics(
                            FileSystemPathSemantics.CurrentHostDefault.Syntax,
                            caseSensitivity ?? FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity);
                        return ValueTask.FromResult(AudiobookFilePathIdentity.CreateValid(
                            Path.GetFullPath(path),
                            semantics,
                            FileSystemCaseSensitivityMode.Auto,
                            Path.GetPathRoot(Path.GetFullPath(path))!));
                    });
                identityResolver = identityResolverMock.Object;
            }

            var directoryOwnershipStore = new Mock<ILibraryDirectoryOwnershipStore>();
            directoryOwnershipStore
                .Setup(store => store.EnsureCreatedHierarchyAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<FileSystemPathSemantics>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var moveQueueService = new Mock<IMoveQueueService>();
            moveQueueService.Setup(service => service.EnsureFilesystemMutationAllowedAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<bool>()))
                .Returns(Task.CompletedTask);
            var renameCommitStore = new Mock<IFileRenameCommitStore>(MockBehavior.Strict);
            renameCommitStore.Setup(store => store.CommitOwnerMetadataAsync(
                    It.IsAny<int>(),
                    It.IsAny<IReadOnlyCollection<Guid>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<int, IReadOnlyCollection<Guid>, CancellationToken>(
                    (_, _, cancellationToken) => repo.SaveChangesAsync(cancellationToken));
            var service = new RenameService(
                config.Object,
                fileNaming,
                fileMover.Object,
                repo,
                fileRepository,
                identityResolver,
                mutationCoordinator ?? new FilesystemMutationCoordinator(),
                new LocalFileSystem(),
                NullLogger<RenameService>.Instance,
                semanticsResolver,
                operationCoordinator ?? _operationCoordinator,
                moveQueueServiceOverride ?? moveQueueService.Object,
                directoryOwnershipStore.Object,
                renameCommitStore.Object,
                rootFolderServiceOverride);

            return (service, db, dbName);
        }

        private static AudiobookFile CreateTrackedFile(
            int id,
            int audiobookId,
            string storedPath,
            string? physicalPath = null,
            string? format = "m4b",
            long? size = null)
        {
            var file = new AudiobookFile
            {
                Id = id,
                AudiobookId = audiobookId,
                Path = storedPath,
                Format = format,
                Size = size
            };
            file.ApplyPhysicalObjectIdentity(
                GetPhysicalObjectIdentity(physicalPath ?? storedPath),
                DateTime.UtcNow);
            return file;
        }

        private static string GetPhysicalObjectIdentity(string path)
        {
            using var lease = PinnedAudiobookFileRegistrationLease.Open(path);
            return lease.PhysicalObjectIdentity;
        }

        private static IFileSystemSemanticsResolver BuildSemanticsResolver(FileSystemCaseSensitivity? caseSensitivity)
        {
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(r => r.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, mode, _) =>
                {
                    var sensitivity = caseSensitivity
                        ?? (mode == FileSystemCaseSensitivityMode.Insensitive
                            ? FileSystemCaseSensitivity.Insensitive
                            : mode == FileSystemCaseSensitivityMode.Sensitive
                                ? FileSystemCaseSensitivity.Sensitive
                                : FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity);
                    return ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(FileSystemPathSemantics.CurrentHostDefault.Syntax, sensitivity),
                        PathIdentityState.Valid,
                        path));
                });
            return resolver.Object;
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

        private static RenamePathSemanticsSnapshot ExpectedSemantics(
            string boundaryPath,
            FileSystemCaseSensitivity? caseSensitivity = null) =>
            new()
            {
                Syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax,
                CaseSensitivity = caseSensitivity
                    ?? FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
                RequestedMode = FileSystemCaseSensitivityMode.Auto,
                BoundaryPath = NormalizePath(boundaryPath)
            };

        private static bool PathsEqualForTest(string left, string right) =>
            FileSystemPathSemantics.CurrentHostDefault.Comparer.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right));

        private static string NormalizePath(string? path) => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path);
    }
}
