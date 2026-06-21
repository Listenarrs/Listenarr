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
using Listenarr.Infrastructure.Persistence.Converters;

namespace Listenarr.Infrastructure.Persistence.Configurations
{
    public class DownloadProcessingJobConfiguration : IEntityTypeConfiguration<DownloadProcessingJob>
    {
        public void Configure(EntityTypeBuilder<DownloadProcessingJob> builder)
        {
            builder.HasKey(j => j.Id);
            builder.Property(j => j.ActiveDeduplicationKey).HasMaxLength(256);
            builder.HasIndex(j => j.ActiveDeduplicationKey)
                .IsUnique()
                .HasFilter("\"ActiveDeduplicationKey\" IS NOT NULL");

            // Map JobData dictionary to a JSON TEXT column with centralized converter + comparer.
            var converter = new JsonValueConverter<Dictionary<string, object>>();
            var comparer = JsonValueComparer.Create<Dictionary<string, object>>();

            var jobDataProp = builder.Property(j => j.JobData)
                .HasConversion(converter)
                .HasColumnName("JobData")
                .HasColumnType("TEXT")
                .IsRequired();

            jobDataProp.Metadata.SetValueComparer(comparer);
        }
    }
}
