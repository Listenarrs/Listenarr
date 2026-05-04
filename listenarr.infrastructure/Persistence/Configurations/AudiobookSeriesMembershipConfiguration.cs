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
    public class AudiobookSeriesMembershipConfiguration : IEntityTypeConfiguration<AudiobookSeriesMembership>
    {
        public void Configure(EntityTypeBuilder<AudiobookSeriesMembership> builder)
        {
            builder.ToTable("AudiobookSeriesMemberships");

            builder.Property(m => m.SeriesName)
                .HasMaxLength(512);

            builder.Property(m => m.SeriesNumber)
                .HasMaxLength(128);

            builder.Property(m => m.SeriesAsin)
                .HasMaxLength(64);

            builder.HasIndex(m => m.AudiobookId);
            builder.HasIndex(m => new { m.AudiobookId, m.SortOrder });
            builder.HasIndex(m => new { m.AudiobookId, m.IsPrimary });

            builder.HasOne(m => m.Audiobook)
                .WithMany(a => a.SeriesMemberships)
                .HasForeignKey(m => m.AudiobookId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
