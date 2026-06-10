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
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models.Naming;

namespace Listenarr.Tests.Features.Api.Services
{
    /// <summary>
    /// Tests for the unified naming orchestrator (BuildDirectory/BuildPath) that every flow now routes
    /// through. These lock in the canonical behavior chosen during the naming-pattern consolidation.
    /// </summary>
    [Trait("Category", "FileNamingService")]
    public class FileNamingService_OrchestratorTests
    {
        private readonly FileNamingService _service =
            new(new Mock<IConfigurationService>().Object, new Mock<ILogger<FileNamingService>>().Object);

        [Fact]
        public void BuildDirectory_LowercaseTokens_ResolveCaseInsensitively()
        {
            // Given a pattern using lowercase tokens
            var settings = new ApplicationSettings { FolderNamingPattern = "{author}/{title}" };
            var ctx = NamingContext.From(new Audiobook { Title = "The Gunslinger", Authors = new() { "Stephen King" } });

            // When / Then the case-insensitive variable dictionary still resolves them
            Assert.Equal(Path.Join("Stephen King", "The Gunslinger"), _service.BuildDirectory(ctx, settings));
        }

        [Fact]
        public void BuildDirectory_TitleWithColon_UsesStrongSanitizer()
        {
            // Given a title containing a colon
            var settings = new ApplicationSettings { FolderNamingPattern = "{Author}/{Title}" };
            var ctx = NamingContext.From(new Audiobook { Title = "Vol 1: The Beginning", Authors = new() { "Jane Doe" } });

            // When / Then the single canonical sanitizer renders ":" as " - " (not "_")
            Assert.Equal(Path.Join("Jane Doe", "Vol 1 - The Beginning"), _service.BuildDirectory(ctx, settings));
        }

        [Fact]
        public void From_AudioMetadata_AuthorNarratedBook_KeepsAuthor()
        {
            // A memoir whose author also narrates it has Artist == Narrator. The author must be kept,
            // not collapsed to "Unknown Author" (the auto-import path takes Artist directly).
            var settings = new ApplicationSettings { FolderNamingPattern = "{Author}/{Title}" };
            var ctx = NamingContext.From(new AudioMetadata { Title = "Bossypants", Artist = "Tina Fey", Narrator = "Tina Fey" });

            Assert.Equal(Path.Join("Tina Fey", "Bossypants"), _service.BuildDirectory(ctx, settings));
        }

        [Fact]
        public void BuildDirectory_NoAuthor_FallsBackToUnknownAuthor()
        {
            var settings = new ApplicationSettings { FolderNamingPattern = "{Author}/{Title}" };
            var ctx = NamingContext.From(new Audiobook { Title = "Orphan" });

            Assert.Equal(Path.Join("Unknown Author", "Orphan"), _service.BuildDirectory(ctx, settings));
        }

        [Fact]
        public void BuildDirectory_EmptySeries_CollapsesSeparators()
        {
            var settings = new ApplicationSettings { FolderNamingPattern = "{Author}/{Series}/{Title}" };
            var ctx = NamingContext.From(new Audiobook { Title = "Standalone", Authors = new() { "Jane Doe" } });

            // Empty {Series} is removed along with its surrounding separator.
            Assert.Equal(Path.Join("Jane Doe", "Standalone"), _service.BuildDirectory(ctx, settings));
        }

        [Fact]
        public void BuildPath_MultiFile_NoNumberToken_AppendsSequenceSuffix()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}", // deliberately no Disk/Chapter token
            };
            var ctx = NamingContext.From(new Audiobook { Title = "Book", Authors = new() { "Author" } });

            var result = _service.BuildPath(ctx, settings, new NamingOptions
            {
                OutputRoot = "/audiobooks",
                IsMultiFile = true,
                SequenceNumber = 3,
                Extension = ".mp3",
            });

            Assert.EndsWith("Book-03.mp3", result.RelativePath);
        }

        [Fact]
        public void BuildPath_CustomBasePath_AppliesFileOnly()
        {
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}/{Series}",
                FileNamingPattern = "{Title}",
            };
            var ctx = NamingContext.From(new Audiobook { Title = "Book", Authors = new() { "Author" }, Series = "Series" });

            var result = _service.BuildPath(ctx, settings, new NamingOptions
            {
                OutputRoot = "/custom/base",
                IsCustomBasePath = true,
                Extension = ".m4b",
            });

            // The folder pattern is skipped under a custom base path — only the file name is produced.
            Assert.Equal("Book.m4b", result.RelativePath);
        }

        [Fact]
        public void BuildDirectory_AgreesWithBuildPathDirectory_AcrossPatternConfigs()
        {
            // Review item 7: BuildDirectory (folder) must agree with BuildPath's directory portion,
            // including when only a (multi-segment) FileNamingPattern is set.
            var ctx = NamingContext.From(new Audiobook
            {
                Title = "The Gunslinger",
                Authors = new() { "Stephen King" },
                Series = "The Dark Tower",
                SeriesNumber = "1",
            });

            var configs = new[]
            {
                new ApplicationSettings { FolderNamingPattern = "{Author}/{Series}/{Title}", FileNamingPattern = "{Title}" },
                new ApplicationSettings { FolderNamingPattern = "", FileNamingPattern = "{Author}/{Series}/{Title}" },
            };

            foreach (var settings in configs)
            {
                var dir = _service.BuildDirectory(ctx, settings);
                var path = _service.BuildPath(ctx, settings, new NamingOptions { OutputRoot = string.Empty, Extension = ".m4b" });
                Assert.Equal(Path.GetDirectoryName(path.RelativePath) ?? string.Empty, dir);
            }
        }

        [Fact]
        public void BuildPath_EmptyFolderPattern_UsesFilePatternAsFullRelativePath()
        {
            var ctx = NamingContext.From(new Audiobook { Title = "The Gunslinger", Authors = new() { "Stephen King" }, Series = "The Dark Tower" });
            var settings = new ApplicationSettings { FolderNamingPattern = "", FileNamingPattern = "{Author}/{Series}/{Title}" };

            var result = _service.BuildPath(ctx, settings, new NamingOptions { OutputRoot = string.Empty, Extension = ".m4b" });

            Assert.Equal(Path.Join("Stephen King", "The Dark Tower", "The Gunslinger") + ".m4b", result.RelativePath);
        }

        [Fact]
        public void BuildPath_EmptyFolderAndFilePattern_FallsBackToAuthorSeriesTitle()
        {
            var ctx = NamingContext.From(new Audiobook { Title = "The Gunslinger", Authors = new() { "Stephen King" }, Series = "The Dark Tower" });
            var settings = new ApplicationSettings { FolderNamingPattern = "", FileNamingPattern = "" };

            var result = _service.BuildPath(ctx, settings, new NamingOptions { OutputRoot = string.Empty, Extension = ".m4b" });

            Assert.Equal(Path.Join("Stephen King", "The Dark Tower", "The Gunslinger") + ".m4b", result.RelativePath);
        }

        [Fact]
        public void BuildPath_MultiFile_WithDiskNumberToken_RendersNumberWithoutExtraSuffix()
        {
            var ctx = NamingContext.From(new Audiobook { Title = "Book", Authors = new() { "Author" } }) with { DiskNumber = 3 };
            var settings = new ApplicationSettings
            {
                OutputPath = "/audiobooks",
                FolderNamingPattern = "{Author}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}",
            };

            var result = _service.BuildPath(ctx, settings, new NamingOptions
            {
                OutputRoot = "/audiobooks",
                IsMultiFile = true,
                SequenceNumber = 3,
                Extension = ".mp3",
            });

            Assert.EndsWith("Book-03.mp3", result.RelativePath);
            Assert.DoesNotContain("Book-03-03", result.RelativePath); // token renders; no extra suffix appended
        }

        [Fact]
        public void From_AudiobookWithNullAuthors_DoesNotThrow_AndUsesUnknownAuthor()
        {
            var settings = new ApplicationSettings { FolderNamingPattern = "{Author}/{Title}" };
            var ctx = NamingContext.From(new Audiobook { Title = "Orphan", Authors = null });

            Assert.Equal(Path.Join("Unknown Author", "Orphan"), _service.BuildDirectory(ctx, settings));
        }

        [Fact]
        public void From_AudibleBookMetadata_MapsAuthorSeriesTitle()
        {
            // LibraryAdd source: the provider metadata (via ToAudiobook) drives the name.
            var settings = new ApplicationSettings { FolderNamingPattern = "{Author}/{Series}/{Title}" };
            var ctx = NamingContext.From(new AudibleBookMetadata
            {
                Title = "The Gunslinger",
                Authors = new() { "Stephen King" },
                Series = "The Dark Tower",
            });

            Assert.Equal(Path.Join("Stephen King", "The Dark Tower", "The Gunslinger"), _service.BuildDirectory(ctx, settings));
        }
    }
}
