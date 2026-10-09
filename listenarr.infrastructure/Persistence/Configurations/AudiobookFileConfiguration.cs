using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class AudiobookFileConfiguration : IEntityTypeConfiguration<AudiobookFile>
{
    public void Configure(EntityTypeBuilder<AudiobookFile> builder)
    {
        builder.Property(file => file.CanonicalPath).HasMaxLength(4096);
        builder.Property(file => file.PathSyntax).HasConversion<string>().HasMaxLength(16);
        builder.Property(file => file.PathCaseSensitivity)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(FileSystemCaseSensitivity.Unknown);
        builder.Property(file => file.PathCaseSensitivityMode)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(FileSystemCaseSensitivityMode.Auto);
        builder.Property(file => file.PathIdentityBoundary).HasMaxLength(4096);
        builder.Property(file => file.PathIdentityLookupKey).HasMaxLength(160);
        builder.Property(file => file.PathOwnershipKey).HasMaxLength(160);
        builder.Property(file => file.PathIdentityState)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(PathIdentityState.Unavailable)
            .HasSentinel(PathIdentityState.Unavailable);
        builder.Property(file => file.PathIdentityReason).HasMaxLength(1024);
        builder.Property(file => file.PathIdentityVersion).HasDefaultValue(1);
        builder.Property(file => file.PhysicalObjectIdentity).HasMaxLength(512);
        builder.Property(file => file.PhysicalIdentityVersion).HasDefaultValue(1);
        // PhysicalIdentityObservedAtUtc is UTC-by-contract
        // (AudiobookFile.ApplyPhysicalObjectIdentity rejects any other kind), but
        // SQLite has no DateTimeKind and materializes DateTime as Unspecified.
        // Restore the invariant at the persistence boundary so consumers never
        // see a loaded entity that violates its own contract.
        builder.Property(file => file.PhysicalIdentityObservedAtUtc)
            .HasConversion(new ValueConverter<DateTime, DateTime>(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc)));

        builder.HasIndex(file => file.PathIdentityLookupKey);
        builder.HasIndex(file => file.PathOwnershipKey)
            .IsUnique()
            .HasFilter("\"PathOwnershipKey\" IS NOT NULL");
    }
}
