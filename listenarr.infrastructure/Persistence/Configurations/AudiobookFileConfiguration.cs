using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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
        builder.Property(file => file.MetadataRescanFailureCount).HasDefaultValue(0);
        builder.Property(file => file.MetadataRescanUnavailableCount).HasDefaultValue(0);
        builder.Property(file => file.MetadataRescanBlockedCount).HasDefaultValue(0);
        builder.Property(file => file.MetadataRescanLastOutcome).HasMaxLength(32);

        builder.HasIndex(file => file.PathIdentityLookupKey);
        builder.HasIndex(file => file.PathOwnershipKey)
            .IsUnique()
            .HasFilter("\"PathOwnershipKey\" IS NOT NULL");
        // Only files missing metadata are indexed. The filter is the text EF
        // emits for the repository's MissingMetadata predicate, in its term
        // order: SQLite uses a partial index only when the query's WHERE
        // clause carries the same terms.
        builder.HasIndex(file => file.MetadataRescanDueUtc)
            .HasDatabaseName("IX_AudiobookFiles_MetadataRescanDue")
            .HasFilter(
                "\"DurationSeconds\" IS NULL OR \"Format\" IS NULL OR \"SampleRate\" IS NULL OR \"Size\" IS NULL OR \"Size\" = 64");
    }
}
