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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Import
{
    /// <summary>
    /// DownloadImportService.cs, the dictionary built per file during an import. Of the five
    /// case-sensitive naming tables this is the one a user actually meets, because it runs on
    /// every automatic import: a library renamed successfully with a lowercase pattern then took
    /// new downloads in under a filename with every token missing.
    /// </summary>
    [Trait("Name", "ImportNamingTableCasingTests")]
    [Trait("Category", "Integration")]
    public sealed class ImportNamingTableCasingTests : BaseTests
    {
        private string _outputRoot = "";

        private readonly Audiobook _audiobook = new AudiobookBuilder()
            .WithTitle("The Wonderful Wizard of Oz")
            .WithAuthor("L. Frank Baum")
            .WithId(321)
            .WithYear("1900")
            .Build();

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            _outputRoot = FileService.GetTempDirectory("casing-out");
            await AddAuthorizedRootAsync(FileService.GetTempPath());
        }

        private async Task SaveSettingsAsync(string filePattern)
        {
            var settings = new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithMultiFileNamingPattern(filePattern)
                .WithFileNamingPattern(filePattern)
                .Build();
            settings.OutputPath = _outputRoot;

            var current = await _applicationSettingsRepository.GetAsync();
            if (current != null)
            {
                settings.Version = current.Version;
                settings.Id = current.Id;
            }

            await _applicationSettingsRepository.SaveAsync(settings);
        }

        [Theory]
        [InlineData("{Author} - {Title}")]
        [InlineData("{author} - {title}")]
        [InlineData("{AUTHOR} - {TITLE}")]
        public async Task ImportedFilename_IsTheSameWhateverCaseThePatternUses(string filePattern)
        {
            await SaveSettingsAsync(filePattern);

            _audiobook.BasePath = _outputRoot;
            await _audiobookRepository.AddAsync(_audiobook);

            var sourceDir = FileService.GetTempDirectory("casing-src");
            var sourceFile = await FileService.GetFileAsync(sourceDir, "incoming.m4b");

            var importService = _provider.GetRequiredService<IDownloadImportService>();
            var results = await importService.ImportDownloadFilesAsync(_audiobook, [sourceFile]);

            var imported = Assert.Single(results);
            Assert.True(imported.Success);
            Assert.NotNull(imported.FinalPath);

            // Both tokens render, whatever case the pattern used. With a case-sensitive table
            // the lowercase and uppercase rows lose both lookups, the sentinel cleanup strips
            // the separator with them, and the file lands as bare ".m4b".
            Assert.Equal(
                "L. Frank Baum - The Wonderful Wizard of Oz.m4b",
                Path.GetFileName(imported.FinalPath!));
        }
    }
}
