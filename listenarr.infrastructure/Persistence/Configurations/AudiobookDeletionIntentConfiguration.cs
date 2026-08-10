using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class AudiobookDeletionIntentConfiguration :
    IEntityTypeConfiguration<AudiobookDeletionIntent>
{
    public void Configure(EntityTypeBuilder<AudiobookDeletionIntent> builder)
    {
        builder.ToTable("AudiobookDeletionIntents");
        builder.HasKey(intent => intent.Id);
        builder.Property(intent => intent.State).HasConversion<string>().HasMaxLength(64);
        builder.Property(intent => intent.Error).HasMaxLength(2048);
        builder.HasIndex(intent => new { intent.AudiobookId, intent.State });
        builder.HasIndex(intent => intent.AudiobookId)
            .IsUnique()
            .HasFilter("\"State\" <> 'Completed'");
        builder.HasIndex(intent => intent.UpdatedAt);
    }
}
