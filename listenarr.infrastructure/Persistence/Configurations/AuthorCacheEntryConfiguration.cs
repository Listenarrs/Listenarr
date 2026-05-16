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
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations
{
    public class AuthorCacheEntryConfiguration : IEntityTypeConfiguration<AuthorCacheEntry>
    {
        public void Configure(EntityTypeBuilder<AuthorCacheEntry> builder)
        {
            builder.HasKey(entry => entry.Id);

            builder.Property(entry => entry.AuthorName)
                .IsRequired()
                .HasMaxLength(256);

            builder.Property(entry => entry.AuthorNameNormalized)
                .IsRequired()
                .HasMaxLength(256);

            builder.Property(entry => entry.AuthorAsin)
                .HasMaxLength(32);

            builder.Property(entry => entry.Region)
                .IsRequired()
                .HasMaxLength(16);

            builder.Property(entry => entry.ImageUrl)
                .HasMaxLength(2048);

            var similarAuthorsConverter =
                (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<CachedRelatedAuthor>?, string>)
                new JsonValueConverter<List<CachedRelatedAuthor>?>();
            var similarAuthorsComparer = JsonValueComparer.Create<List<CachedRelatedAuthor>?>();
            var similarAuthorsProp = builder.Property(entry => entry.SimilarAuthors)
                .HasConversion(similarAuthorsConverter)
                .HasColumnType("TEXT");
            similarAuthorsProp.Metadata.SetValueComparer(similarAuthorsComparer);

            var catalogBooksConverter =
                (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<CachedAuthorCatalogBook>?, string>)
                new JsonValueConverter<List<CachedAuthorCatalogBook>?>();
            var catalogBooksComparer = JsonValueComparer.Create<List<CachedAuthorCatalogBook>?>();
            var catalogBooksProp = builder.Property(entry => entry.CatalogBooks)
                .HasConversion(catalogBooksConverter)
                .HasColumnType("TEXT");
            catalogBooksProp.Metadata.SetValueComparer(catalogBooksComparer);

            builder.HasIndex(entry => new { entry.AuthorNameNormalized, entry.Region })
                .IsUnique();

            builder.HasIndex(entry => new { entry.AuthorAsin, entry.Region });
        }
    }
}
