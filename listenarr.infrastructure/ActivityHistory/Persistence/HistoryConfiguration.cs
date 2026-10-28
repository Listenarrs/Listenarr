/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.ActivityHistory.Persistence
{
    public sealed class HistoryConfiguration : IEntityTypeConfiguration<History>
    {
        public void Configure(EntityTypeBuilder<History> builder)
        {
            builder.Property(h => h.EventType).IsRequired().HasMaxLength(100);
            builder.Property(h => h.SourceTitle).HasMaxLength(500);
            builder.Property(h => h.AudiobookExternalId).HasMaxLength(64);
            builder.Property(h => h.Source).HasMaxLength(100);
            builder.Property(h => h.DownloadId).HasMaxLength(150);
            builder.Property(h => h.DownloadClientId).HasMaxLength(100);
            builder.Property(h => h.CorrelationId).IsRequired().HasMaxLength(64);
            builder.Property(h => h.IdempotencyKey).HasMaxLength(200);
            builder.Property(h => h.Error).HasMaxLength(4000);
            builder.Property(h => h.Outcome).HasConversion<int>();
        }
    }
}
