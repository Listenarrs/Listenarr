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

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class MoveJobConfiguration : IEntityTypeConfiguration<MoveJob>
{
    public void Configure(EntityTypeBuilder<MoveJob> builder)
    {
        builder.Property(job => job.ActiveDeduplicationKey).HasMaxLength(1024);
        builder.HasIndex(job => job.ActiveDeduplicationKey)
            .IsUnique()
            .HasFilter("\"ActiveDeduplicationKey\" IS NOT NULL");
    }
}
