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

namespace Listenarr.Infrastructure.Persistence.Configurations
{
    public class MonitoredAuthorConfiguration : IEntityTypeConfiguration<MonitoredAuthor>
    {
        public void Configure(EntityTypeBuilder<MonitoredAuthor> builder)
        {
            builder.HasKey(author => author.Id);

            builder.Property(author => author.AuthorName)
                .IsRequired()
                .HasMaxLength(256);

            builder.Property(author => author.AuthorNameNormalized)
                .IsRequired()
                .HasMaxLength(256);

            builder.Property(author => author.AuthorAsin)
                .HasMaxLength(32);

            builder.Property(author => author.Region)
                .IsRequired()
                .HasMaxLength(16);

            builder.Property(author => author.Language)
                .IsRequired()
                .HasMaxLength(32);

            builder.Property(author => author.LastError)
                .HasMaxLength(2048);
        }
    }
}
