using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Tests
{
    public class ImportServiceTests
    {
        [Fact]
        public async Task ImportFilesFromDirectory_CreatesDestinationDirectory_WhenMissing()
        {
            // Arrange
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);

            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var file1 = Path.Join(sourceDir, "track1.m4b");
            var file2 = Path.Join(sourceDir, "track2.m4b");
            await File.WriteAllTextAsync(file1, "dummy");
            await File.WriteAllTextAsync(file2, "dummy");

            var settings = new ApplicationSettings { OutputPath = outputRoot, CompletedFileAction = "Move", EnableMetadataProcessing = false };

            // Build provider and register ImportService with an in-memory DB factory
            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options;

                var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
                dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                    .ReturnsAsync(new ListenArrDbContext(options));

                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<IFileNamingService>(new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()));
                services.AddSingleton<IMetadataService>(new Mock<IMetadataService>().Object);
                // ImportService uses NullFileMover by default when not provided, which is fine for tests
                services.AddSingleton<IImportService>(sp => new ImportService(dbFactoryMock.Object, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IFileNamingService>(), sp.GetService<IMetadataService>(), new NullLogger<ImportService>()));
            });

            var importService = provider.GetRequiredService<IImportService>();

            // Act
            var results = await importService.ImportFilesFromDirectoryAsync("dl-1", null, new[] { file1, file2 }, settings);

            // Assert: destination directory created
            Assert.True(Directory.Exists(outputRoot));

            // At least one successful import result should be present
            Assert.Contains(results, r => r.Success);

            // All successful results should point to files under the output root
            foreach (var r in results.Where(r => r.Success))
            {
                Assert.StartsWith(outputRoot.TrimEnd(Path.DirectorySeparatorChar), r.FinalPath, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(r.FinalPath));
            }

            // Cleanup
            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportSingleFile_WithAudiobookBasePath_DoesNotDuplicateFolderPatternSegments()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            var basePath = Path.Join(outputRoot, "Frank Herbert", "Dune");
            Directory.CreateDirectory(basePath);

            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Join(sourceDir, "dune-source.m4b");
            await File.WriteAllTextAsync(sourceFile, "dummy");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                FileNamingPattern = "{Author}/{Title}/{Title} ({Year})"
            };

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var seed = new ListenArrDbContext(options))
            {
                seed.Audiobooks.Add(new Audiobook
                {
                    Id = 123,
                    Title = "Dune",
                    Authors = new System.Collections.Generic.List<string> { "Frank Herbert" },
                    PublishYear = "2021",
                    BasePath = basePath
                });
                await seed.SaveChangesAsync();
            }

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<IFileNamingService>(new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()));
                services.AddSingleton<IMetadataService>(new Mock<IMetadataService>().Object);
                services.AddSingleton<IImportService>(sp => new ImportService(
                    dbFactoryMock.Object,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<IFileNamingService>(),
                    sp.GetService<IMetadataService>(),
                    new NullLogger<ImportService>()));
            });

            var importService = provider.GetRequiredService<IImportService>();

            var result = await importService.ImportSingleFileAsync("dl-1", 123, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar), result.FinalPath!, StringComparison.OrdinalIgnoreCase);

            var relative = Path.GetRelativePath(basePath, result.FinalPath!);
            Assert.Equal(Path.GetFileName(relative), relative);
            Assert.DoesNotContain($"Frank Herbert{Path.DirectorySeparatorChar}", relative, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Dune{Path.DirectorySeparatorChar}", relative, StringComparison.OrdinalIgnoreCase);

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        private static void TryDeleteDirectory(string path, bool recursive = false)
        {
            try
            {
                Directory.Delete(path, recursive);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
        }
    }
}
