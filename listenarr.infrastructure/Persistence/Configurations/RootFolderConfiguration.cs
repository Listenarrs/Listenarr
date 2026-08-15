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
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations
{
    public class RootFolderConfiguration : IEntityTypeConfiguration<RootFolder>
    {
        public void Configure(EntityTypeBuilder<RootFolder> builder)
        {
            builder.ToTable("RootFolders");

            builder.HasIndex(r => r.Path).IsUnique();
            builder.HasIndex(r => r.Name);
            builder.HasIndex(r => r.IsDefault)
                .IsUnique()
                .HasDatabaseName("IX_RootFolders_SingleDefault")
                .HasFilter("\"IsDefault\" = 1");

            builder.Property(r => r.Name).HasMaxLength(200).IsRequired();
            builder.Property(r => r.Path).HasMaxLength(1000).IsRequired();
            builder.Property(r => r.IsDefault).HasDefaultValue(false);
            builder.Property(r => r.CaseSensitivityMode)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(FileSystemCaseSensitivityMode.Auto);
            builder.Property(r => r.ResolvedCaseSensitivity)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(FileSystemCaseSensitivity.Unknown);
            builder.Property(r => r.PathIdentityState)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(PathIdentityState.Unavailable)
                .HasSentinel(PathIdentityState.Unavailable);
            builder.Property(r => r.DirectoryObjectIdentity).HasMaxLength(256);
            builder.Property(r => r.DirectoryObjectIdentityUnavailableReason).HasMaxLength(1024);
            builder.HasIndex(r => r.PathIdentityKey)
                .IsUnique()
                .HasFilter("\"PathIdentityKey\" IS NOT NULL");

            builder.Property(r => r.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        }
    }
}
