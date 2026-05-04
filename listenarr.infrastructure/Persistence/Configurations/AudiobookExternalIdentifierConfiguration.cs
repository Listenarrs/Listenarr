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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations
{
    public class AudiobookExternalIdentifierConfiguration : IEntityTypeConfiguration<AudiobookExternalIdentifier>
    {
        public void Configure(EntityTypeBuilder<AudiobookExternalIdentifier> builder)
        {
            builder.ToTable("AudiobookExternalIdentifiers");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.Type)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            builder.Property(x => x.Source)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            builder.Property(x => x.ValueRaw)
                .HasMaxLength(512)
                .IsRequired();

            builder.Property(x => x.ValueNormalized)
                .HasMaxLength(256)
                .IsRequired();

            builder.Property(x => x.Region)
                .HasMaxLength(8);

            builder.Property(x => x.IsPrimary)
                .IsRequired();

            builder.Property(x => x.CreatedAt)
                .IsRequired();

            builder.Property(x => x.UpdatedAt)
                .IsRequired();

            builder.HasIndex(x => x.AudiobookId);
            builder.HasIndex(x => new { x.Type, x.ValueNormalized });
            builder.HasIndex(x => new { x.Type, x.ValueNormalized, x.Region });
            builder.HasIndex(x => new { x.AudiobookId, x.Type, x.IsPrimary });
        }
    }
}
