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

namespace Listenarr.Tests.Features.Api.Services
{
    /// <summary>
    /// Which name off a multi-credit byline reaches a folder path.
    /// </summary>
    /// <remarks>
    /// These go through the naming service rather than the credit helper directly, because the
    /// helper passing on its own says nothing about whether anything calls it. The reversed
    /// byline is the case that fails when the first credit is taken positionally, and the
    /// single-author and co-author cases are what stop this from passing against a service that
    /// had simply stopped emitting an author at all.
    /// </remarks>
    [Trait("Name", nameof(FileNamingService_AuthorCreditTests))]
    [Trait("Category", "FileNamingService")]
    public class FileNamingService_AuthorCreditTests : BaseTests
    {
        private readonly FileNamingService _service;

        public FileNamingService_AuthorCreditTests()
        {
            _service = new FileNamingService(
                new Mock<IConfigurationService>().Object,
                new Mock<ILogger<FileNamingService>>().Object);
        }

        private string RenderAuthor(params string[] authors) =>
            _service.ApplyNamingPattern(
                "{Author}",
                new AudibleBookMetadata { Title = "Untitled", Authors = authors.ToList() });

        [Fact]
        public void AuthorFolder_UsesTheAuthorWhenTheTranslatorIsCreditedSecond()
        {
            // B002V9ZF3K, Crime and Punishment.
            Assert.Equal("Fyodor Dostoevsky", RenderAuthor("Fyodor Dostoevsky", "Constance Garnett - translator"));
        }

        [Fact]
        public void AuthorFolder_UsesTheAuthorWhenTheTranslatorIsCreditedFirst()
        {
            // B00EZAXAF8, The Brothers Karamazov, the same pair the other way round. Taking the
            // first credit renders "Constance Garnett - translator" here, so this is the
            // assertion that separates the two rules.
            Assert.Equal("Fyodor Dostoevsky", RenderAuthor("Constance Garnett - translator", "Fyodor Dostoevsky"));
        }

        [Fact]
        public void AuthorFolder_DoesNotDependOnBylineOrder()
        {
            // Stated directly rather than left implicit in the pair above, because order
            // independence is the property being bought and it should fail loudly if lost.
            Assert.Equal(
                RenderAuthor("Jules Verne", "George Makepeace Towle - translator"),
                RenderAuthor("George Makepeace Towle - translator", "Jules Verne"));
        }

        [Fact]
        public void AuthorFolder_IsUnchangedForASingleAuthor()
        {
            // The control against a service that has stopped resolving an author at all: this
            // passes on canary and has to keep passing.
            Assert.Equal("Edgar Rice Burroughs", RenderAuthor("Edgar Rice Burroughs"));
        }

        [Fact]
        public void AuthorFolder_StillTakesTheFirstOfTwoPlainCredits()
        {
            // B007ZEANIS. Neither credit names a role, so there is nothing to prefer between
            // them and the first still wins. Removing roles must not turn into reordering
            // ordinary credits. (These two names are one man, his pen name and his legal name,
            // which is a different problem and not this one.)
            Assert.Equal("O. Henry", RenderAuthor("O. Henry", "William Sydney Porter"));
        }

        [Fact]
        public void AuthorFolder_UsesTheContributorsCleanNameWhenEveryCreditNamesARole()
        {
            // 1094179574, an anthology credited only to its editors. Better the editor's name
            // than "Unknown Author", and better it without the job title in the folder name.
            Assert.Equal("Lisa Morton", RenderAuthor("Lisa Morton - editor", "Leslie S. Klinger - editor"));
        }

        [Fact]
        public void AuthorFolder_DoesNotPutAJobTitleInThePath()
        {
            // The single-credit case, which is the commonest shape of this: a translated classic
            // credited to nobody but its translator would otherwise create a folder called
            // "Stephen Mitchell - translator".
            Assert.Equal("Stephen Mitchell", RenderAuthor("Stephen Mitchell - translator"));
        }
    }
}
