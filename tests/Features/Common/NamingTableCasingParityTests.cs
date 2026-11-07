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
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Common
{
    /// <summary>
    /// Six places build a book's naming variables. The token regex in
    /// FileNamingService.ApplyNamingPattern carries RegexOptions.IgnoreCase, so a pattern
    /// written {author} arrives at the dictionary lookup as "author". Five of the six keyed
    /// their dictionary with the default comparer, so the lookup missed, the not-found path
    /// emitted the empty sentinel, and the cleanup removed the segment: the token rendered as
    /// nothing and the file landed somewhere else.
    ///
    /// RenameService was the one that already had StringComparer.OrdinalIgnoreCase, which is
    /// why renaming a library with a lowercase pattern worked and importing into it did not.
    ///
    /// Ordinal, not culture-aware: under tr-TR 'I' and 'i' are different letters, so
    /// CurrentCultureIgnoreCase would break {TITLE} against the key "Title".
    /// </summary>
    [Trait("Name", "NamingTableCasingParityTests")]
    [Trait("Category", "Unit")]
    public sealed class NamingTableCasingParityTests : BaseTests
    {
        private static FileNamingService Naming() =>
            new(Mock.Of<IConfigurationService>(), NullLogger<FileNamingService>.Instance);

        private static string[] Segments(string path) =>
            path.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
                StringSplitOptions.RemoveEmptyEntries);

        // FileNamingService.Helpers.cs, BuildVariables(AudioMetadata).
        [Theory]
        [InlineData("{Author}/{Series}/{Title}")]
        [InlineData("{author}/{series}/{title}")]
        [InlineData("{AUTHOR}/{SERIES}/{TITLE}")]
        public void AudioMetadataTable_ResolvesATokenInAnyCase(string pattern)
        {
            var metadata = new AudioMetadata
            {
                Title = "The Wonderful Wizard of Oz",
                Artist = "L. Frank Baum",
                Series = "Land of Oz",
            };

            var segments = Segments(Naming().ApplyNamingPattern(pattern, metadata));

            Assert.Equal(3, segments.Length);
            Assert.Contains("L. Frank Baum", segments);
            Assert.Contains("Land of Oz", segments);
            Assert.Contains("The Wonderful Wizard of Oz", segments);
        }

        // FileNamingService.Helpers.cs, BuildVariables(AudibleBookMetadata).
        [Theory]
        [InlineData("{Author}/{Series}/{Title}")]
        [InlineData("{author}/{series}/{title}")]
        [InlineData("{AUTHOR}/{SERIES}/{TITLE}")]
        public void AudibleBookMetadataTable_ResolvesATokenInAnyCase(string pattern)
        {
            var metadata = new AudibleBookMetadata
            {
                Title = "The Wonderful Wizard of Oz",
                Authors = ["L. Frank Baum"],
                Series = "Land of Oz",
            };

            var segments = Segments(Naming().ApplyNamingPattern(pattern, metadata));

            Assert.Equal(3, segments.Length);
            Assert.Contains("L. Frank Baum", segments);
            Assert.Contains("Land of Oz", segments);
            Assert.Contains("The Wonderful Wizard of Oz", segments);
        }

        // LibraryPathPlanner.cs, the directory a book is given when it is added to the library.
        [Theory]
        [InlineData("{Author}/{Series}/{Title}")]
        [InlineData("{author}/{series}/{title}")]
        [InlineData("{AUTHOR}/{SERIES}/{TITLE}")]
        public void LibraryAddTable_ResolvesATokenInAnyCase(string pattern)
        {
            var audiobook = new Audiobook
            {
                Title = "The Wonderful Wizard of Oz",
                Authors = ["L. Frank Baum"],
                Series = "Land of Oz",
            };

            var relative = LibraryPathPlanner.ComputeAudiobookRelativeDirectoryFromPattern(
                audiobook,
                pattern,
                Naming());

            var segments = Segments(relative);

            Assert.Equal(3, segments.Length);
            Assert.Contains("L. Frank Baum", segments);
            Assert.Contains("Land of Oz", segments);
            Assert.Contains("The Wonderful Wizard of Oz", segments);
        }
    }
}
