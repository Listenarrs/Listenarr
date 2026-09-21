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

namespace Listenarr.Tests.Features.Domain.Audiobooks
{
    /// <summary>
    /// What reaches storage when provider metadata becomes a library row.
    /// </summary>
    /// <remarks>
    /// Roles are removed here rather than on the way out, so the stored list is the one every
    /// consumer sees. It also makes the stored string a better key, which is what author
    /// grouping, the Authors page and the by-name ASIN lookup all key off.
    /// </remarks>
    [Trait("Name", nameof(AudibleBookMetadataAuthorCreditTests))]
    [Trait("Category", "AuthorCredits")]
    public class AudibleBookMetadataAuthorCreditTests : BaseTests
    {
        private static AudibleBookMetadata Metadata(params string[] authors) =>
            new() { Title = "Untitled", Asin = "B000000000", Authors = authors.ToList() };

        [Fact]
        public void ToAudiobook_StoresTheTranslatorUnderHerNameRatherThanHerJobTitle()
        {
            var audiobook = Metadata("Fyodor Dostoevsky", "Constance Garnett - translator").ToAudiobook();

            Assert.Equal(new[] { "Fyodor Dostoevsky", "Constance Garnett" }, audiobook.Authors);
        }

        [Fact]
        public void ToAudiobook_StoresTheAuthorFirstEvenWhenTheProviderDidNot()
        {
            // B00EZAXAF8 credits the translator ahead of Dostoevsky. Everybody is kept and
            // everybody is spelled properly, and the author is stored first, because once the
            // role is gone every consumer that takes Authors[0] has no other way to find them.
            var audiobook = Metadata("Constance Garnett - translator", "Fyodor Dostoevsky").ToAudiobook();

            Assert.Equal(new[] { "Fyodor Dostoevsky", "Constance Garnett" }, audiobook.Authors);
        }

        [Fact]
        public void ToAudiobook_LeavesASingleAuthorAlone()
        {
            // Control. Passes before the change as well as after, so a filter that had started
            // emptying the list wholesale would be caught here rather than looking like a pass.
            var audiobook = Metadata("Edgar Rice Burroughs").ToAudiobook();

            Assert.Equal(new[] { "Edgar Rice Burroughs" }, audiobook.Authors);
        }

        [Fact]
        public void ToAudiobook_KeepsBothCreditsWhenNeitherNamesARole()
        {
            // These two names are one man, his pen name and his legal name, which is its own
            // problem and not this one. What matters is that neither credit names a role, so
            // neither may be altered.
            var audiobook = Metadata("O. Henry", "William Sydney Porter").ToAudiobook();

            Assert.Equal(new[] { "O. Henry", "William Sydney Porter" }, audiobook.Authors);
        }

        [Fact]
        public void ToAudiobook_FixesAnAnthologyCreditedToNobodyButItsEditors()
        {
            // The case the rejected rule could not touch. Something had to survive a drop, so it
            // returned the list untouched and the book kept exactly the problem being fixed.
            var audiobook = Metadata("Lisa Morton - editor", "Leslie S. Klinger - editor").ToAudiobook();

            Assert.Equal(new[] { "Lisa Morton", "Leslie S. Klinger" }, audiobook.Authors);
        }

        [Fact]
        public void ToAudiobook_CollapsesADuplicateThatRemovingARoleCreates()
        {
            // Somebody credited twice, once as author and once as translator, must not be
            // stored twice under one name. This is the one problem this rule creates that the
            // rejected one did not.
            var audiobook = Metadata("Samuel Butler", "Samuel Butler - translator").ToAudiobook();

            Assert.Equal(new[] { "Samuel Butler" }, audiobook.Authors);
        }

        [Fact]
        public void ToAudiobook_StillFallsBackToTheSingularAuthorField()
        {
            // The singular Author field is the fallback when no list arrives, and filtering
            // must not have swallowed it.
            var audiobook = new AudibleBookMetadata
            {
                Title = "Untitled",
                Asin = "B000000000",
                Author = "Jules Verne"
            }.ToAudiobook();

            Assert.Equal(new[] { "Jules Verne" }, audiobook.Authors);
        }

        [Fact]
        public void ToAudiobook_DoesNotPopulateAuthorAsins()
        {
            // Stated as what it is rather than dressed up as an ASIN guarantee. An earlier
            // version asserted that AuthorAsins held at most one entry, which was vacuous
            // because ToAudiobook never assigns the field. Resolution happens later, in the add
            // workflow, and nothing here is evidence about it.
            var audiobook = Metadata("Fyodor Dostoevsky", "Constance Garnett - translator").ToAudiobook();

            Assert.Null(audiobook.AuthorAsins);
        }
    }
}
