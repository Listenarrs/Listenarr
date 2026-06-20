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

namespace Listenarr.Tests.Features.Application.Audiobooks.Identifiers
{
    public class AudiobookIdentifierMapperTests
    {
        [Fact]
        public void GetEffectiveIdentifiers_SuppressesImportedLegacyDuplicate_WhenManualValueExists()
        {
            var audiobook = new Audiobook
            {
                Asin = "B0DQR9D4YG",
                ExternalIdentifiers = new List<AudiobookExternalIdentifier>
                {
                    new AudiobookExternalIdentifier
                    {
                        Type = AudiobookExternalIdentifierType.Asin,
                        ValueRaw = "B0DQR9D4YG",
                        ValueNormalized = "B0DQR9D4YG",
                        Region = "us",
                        IsPrimary = true,
                        Source = AudiobookExternalIdentifierSource.Manual
                    }
                }
            };

            var identifiers = AudiobookIdentifierMapper.GetEffectiveIdentifiers(audiobook);

            Assert.Single(identifiers);
            Assert.Equal(AudiobookExternalIdentifierSource.Manual, identifiers[0].Source);
            Assert.Equal("us", identifiers[0].Region);
        }

        [Fact]
        public void SyncImportedIdentifiersFromLegacyFields_AddsNormalizedLegacyIdentifiers()
        {
            var audiobook = new Audiobook
            {
                Asin = "B0DQR9D4YG",
                Isbn = new List<string> { "978-1-4028-9462-6" },
                OpenLibraryId = "OL123M"
            };

            AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(audiobook);

            Assert.Contains(audiobook.ExternalIdentifiers, i =>
                i.Type == AudiobookExternalIdentifierType.Asin &&
                i.ValueNormalized == "B0DQR9D4YG" &&
                i.Source == AudiobookExternalIdentifierSource.Imported);
            Assert.Contains(audiobook.ExternalIdentifiers, i =>
                i.Type == AudiobookExternalIdentifierType.Isbn &&
                i.ValueNormalized == "9781402894626");
            Assert.Contains(audiobook.ExternalIdentifiers, i =>
                i.Type == AudiobookExternalIdentifierType.OpenLibraryId &&
                i.ValueNormalized == "OL123M");
        }
    }
}
