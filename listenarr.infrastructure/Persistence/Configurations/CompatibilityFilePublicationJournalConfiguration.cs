using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

internal sealed class CompatibilityFilePublicationJournalConfiguration
    : IEntityTypeConfiguration<CompatibilityFilePublicationJournal>
{
    public void Configure(
        EntityTypeBuilder<CompatibilityFilePublicationJournal> builder)
    {
        builder.ToTable("CompatibilityFilePublicationJournals");
        builder.HasKey(journal => journal.OperationId);
        builder.Property(journal => journal.SourcePath)
            .IsRequired()
            .HasMaxLength(4096);
        builder.Property(journal => journal.DestinationPath)
            .IsRequired()
            .HasMaxLength(4096);
        builder.Property(journal => journal.SourceSha256)
            .IsRequired()
            .HasMaxLength(64);
        builder.Property(journal => journal.TargetSha256)
            .HasMaxLength(64);
        builder.Property(journal => journal.Error)
            .HasMaxLength(2048);
        builder.HasIndex(journal => journal.State);
        builder.HasIndex(journal => journal.AudiobookId);
    }
}
