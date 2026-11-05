using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class IndexerRequestUsageConfiguration : IEntityTypeConfiguration<IndexerRequestUsage>
{
    public void Configure(EntityTypeBuilder<IndexerRequestUsage> builder)
    {
        builder.ToTable("IndexerRequestUsages");
        builder.HasKey(usage => new { usage.IndexerId, usage.HourStartUtc });
        builder.HasIndex(usage => usage.HourStartUtc);
        builder.HasOne<Indexer>()
            .WithMany()
            .HasForeignKey(usage => usage.IndexerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
