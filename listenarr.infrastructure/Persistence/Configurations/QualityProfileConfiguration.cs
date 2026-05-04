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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence.Converters;

namespace Listenarr.Infrastructure.Persistence.Configurations
{
    public class QualityProfileConfiguration : IEntityTypeConfiguration<QualityProfile>
    {
        public void Configure(EntityTypeBuilder<QualityProfile> builder)
        {
            builder.HasKey(q => q.Id);

            // Serialize Qualities list into a JSON TEXT column to avoid creating a separate entity type.
            var converter = new JsonValueConverter<List<QualityDefinition>>();
            var comparer = JsonValueComparer.Create<List<QualityDefinition>>();

            var prop = builder.Property(q => q.Qualities)
                .HasConversion(converter)
                .HasColumnName("Qualities")
                .HasColumnType("TEXT")
                .IsRequired();

            prop.Metadata.SetValueComparer(comparer);

            // For other string-list properties used across the profile, prefer JSON mapping if stored as TEXT.
            var preferredFormatsConverter = new JsonValueConverter<List<string>>();
            var preferredFormatsComparer = JsonValueComparer.Create<List<string>>();
            var pfProp = builder.Property(q => q.PreferredFormats)
                .HasConversion(preferredFormatsConverter)
                .HasColumnName("PreferredFormats")
                .HasColumnType("TEXT")
                .IsRequired();

            pfProp.Metadata.SetValueComparer(preferredFormatsComparer);

            var preferredLanguagesConverter = new JsonValueConverter<List<string>>();
            var preferredLanguagesComparer = JsonValueComparer.Create<List<string>>();
            var plProp = builder.Property(q => q.PreferredLanguages)
                .HasConversion(preferredLanguagesConverter)
                .HasColumnName("PreferredLanguages")
                .HasColumnType("TEXT")
                .IsRequired();

            plProp.Metadata.SetValueComparer(preferredLanguagesComparer);

            var mustContainConverter = new JsonValueConverter<List<string>>();
            var mustContainComparer = JsonValueComparer.Create<List<string>>();
            var mustContainProp = builder.Property(q => q.MustContain)
                .HasConversion(mustContainConverter)
                .HasColumnName("MustContain")
                .HasColumnType("TEXT")
                .IsRequired();

            mustContainProp.Metadata.SetValueComparer(mustContainComparer);

            var mustNotContainConverter = new JsonValueConverter<List<string>>();
            var mustNotContainComparer = JsonValueComparer.Create<List<string>>();
            var mustNotContainProp = builder.Property(q => q.MustNotContain)
                .HasConversion(mustNotContainConverter)
                .HasColumnName("MustNotContain")
                .HasColumnType("TEXT")
                .IsRequired();

            mustNotContainProp.Metadata.SetValueComparer(mustNotContainComparer);

            // Configure CustomGroupNames as JSON dictionary (optional field)
            var customGroupNamesConverter = new JsonValueConverter<Dictionary<string, string>?>();
            var customGroupNamesComparer = JsonValueComparer.Create<Dictionary<string, string>?>();
            var customGroupNamesProp = builder.Property(q => q.CustomGroupNames)
                .HasConversion(customGroupNamesConverter)
                .HasColumnName("CustomGroupNames")
                .HasColumnType("TEXT")
                .IsRequired(false);

            customGroupNamesProp.Metadata.SetValueComparer(customGroupNamesComparer);
        }
    }
}
