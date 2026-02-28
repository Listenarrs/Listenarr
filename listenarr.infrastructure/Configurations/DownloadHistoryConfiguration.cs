/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Linq;
using System.Text.Json;

namespace Listenarr.Infrastructure.Configurations
{
    public class DownloadHistoryConfiguration : IEntityTypeConfiguration<DownloadHistory>
    {
        public void Configure(EntityTypeBuilder<DownloadHistory> builder)
        {
            builder.ToTable("DownloadHistories");

            builder.HasKey(dh => dh.Id);

            builder.Property(dh => dh.DownloadId)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(dh => dh.EventType)
                .IsRequired()
                .HasConversion<int>();

            builder.Property(dh => dh.Status)
                .IsRequired()
                .HasConversion<int>();

            builder.Property(dh => dh.EventDate)
                .IsRequired();

            builder.Property(dh => dh.AudiobookId)
                .IsRequired(false);

            builder.Property(dh => dh.DownloadClient)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(dh => dh.DownloadClientId)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(dh => dh.Protocol)
                .IsRequired()
                .HasConversion<int>();

            builder.Property(dh => dh.Title)
                .IsRequired()
                .HasMaxLength(500);

            builder.Property(dh => dh.OutputPath)
                .HasMaxLength(1000);

            // Serialize Data dictionary as JSON
            builder.Property(dh => dh.Data)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(v, (JsonSerializerOptions?)null)
                )
                .HasColumnType("TEXT")
                .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, object>?>(
                    (a, b) => a == null && b == null || a != null && b != null && a.SequenceEqual(b),
                    a => a == null ? 0 : a.Aggregate(0, (acc, x) => unchecked(acc * 397 ^ (x.Key.GetHashCode() ^ (x.Value == null ? 0 : x.Value.GetHashCode())))),
                    a => a == null ? null : a.ToDictionary(x => x.Key, x => x.Value)
                ));

            builder.Property(dh => dh.ErrorMessage)
                .HasMaxLength(2000);

            builder.Property(dh => dh.WasImported)
                .IsRequired()
                .HasDefaultValue(false);

            builder.Property(dh => dh.ImportedAt)
                .IsRequired(false);

            // Indexes are defined in DbContext OnModelCreating
        }
    }
}
