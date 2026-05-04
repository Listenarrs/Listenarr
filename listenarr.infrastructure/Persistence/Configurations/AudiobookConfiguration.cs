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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence.Converters;

namespace Listenarr.Infrastructure.Persistence.Configurations
{
    /// <summary>
    /// EF mapping for Audiobook entity extracted from ListenArrDbContext.
    /// Keeps conversions / comparers and relationships colocated for easier testing & reuse.
    /// </summary>
    public class AudiobookConfiguration : IEntityTypeConfiguration<Audiobook>
    {
        private static ValueComparer<List<string>> StringListComparer() =>
            new ValueComparer<List<string>>(
                (c1, c2) => (c1 ?? new List<string>()).SequenceEqual(c2 ?? new List<string>()),
                c => (c ?? new List<string>()).Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c == null ? new List<string>() : c.ToList()
            );

        public void Configure(EntityTypeBuilder<Audiobook> builder)
        {
            // Map list-of-string properties as JSON TEXT columns so EF will properly
            // deserialize the JSON arrays that migrations now produce (e.g. ["Name"]).
            var authorsConverter = (ValueConverter<List<string>?, string>)new JsonValueConverter<List<string>?>();
            var authorsComparer = JsonValueComparer.Create<List<string>?>();
            var authorsProp = builder.Property(e => e.Authors)
                .HasConversion(authorsConverter)
                .HasColumnType("TEXT");
            authorsProp.Metadata.SetValueComparer(authorsComparer);

            var genresConverter = (ValueConverter<List<string>?, string>)new JsonValueConverter<List<string>?>();
            var genresComparer = JsonValueComparer.Create<List<string>?>();
            var genresProp = builder.Property(e => e.Genres)
                .HasConversion(genresConverter)
                .HasColumnType("TEXT");
            genresProp.Metadata.SetValueComparer(genresComparer);

            var tagsConverter = (ValueConverter<List<string>?, string>)new JsonValueConverter<List<string>?>();
            var tagsComparer = JsonValueComparer.Create<List<string>?>();
            var tagsProp = builder.Property(e => e.Tags)
                .HasConversion(tagsConverter)
                .HasColumnType("TEXT");
            tagsProp.Metadata.SetValueComparer(tagsComparer);

            var narratorsConverter = (ValueConverter<List<string>?, string>)new JsonValueConverter<List<string>?>();
            var narratorsComparer = JsonValueComparer.Create<List<string>?>();
            var narratorsProp = builder.Property(e => e.Narrators)
                .HasConversion(narratorsConverter)
                .HasColumnType("TEXT");
            narratorsProp.Metadata.SetValueComparer(narratorsComparer);

            var isbnConverter = (ValueConverter<List<string>?, string>)new JsonValueConverter<List<string>?>();
            var isbnComparer = JsonValueComparer.Create<List<string>?>();
            var isbnProp = builder.Property(e => e.Isbn)
                .HasConversion(isbnConverter)
                .HasColumnType("TEXT");
            isbnProp.Metadata.SetValueComparer(isbnComparer);

            // Author ASINs (resolve/cached author images rely on stored ASINs)
            var authorAsinsConverter = (ValueConverter<List<string>?, string>)new JsonValueConverter<List<string>?>();
            var authorAsinsComparer = JsonValueComparer.Create<List<string>?>();
            var authorAsinsProp = builder.Property(e => e.AuthorAsins)
                .HasConversion(authorAsinsConverter)
                .HasColumnType("TEXT");
            authorAsinsProp.Metadata.SetValueComparer(authorAsinsComparer);

            // One-to-many: Audiobook -> AudiobookFiles
            builder.HasMany(a => a.Files)
                .WithOne(f => f.Audiobook)
                .HasForeignKey(f => f.AudiobookId)
                .OnDelete(DeleteBehavior.Cascade);

            // One-to-many: Audiobook -> ExternalIdentifiers
            builder.HasMany(a => a.ExternalIdentifiers)
                .WithOne()
                .HasForeignKey(i => i.AudiobookId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(a => a.SeriesMemberships)
                .WithOne(m => m.Audiobook)
                .HasForeignKey(m => m.AudiobookId)
                .OnDelete(DeleteBehavior.Cascade);

            // Performance indexes commonly used by queries
            builder.HasIndex(a => a.Monitored);
            builder.HasIndex(a => a.LastSearchTime);
        }
    }
}
