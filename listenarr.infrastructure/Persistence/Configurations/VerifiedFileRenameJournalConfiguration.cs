using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

internal sealed class VerifiedFileRenameJournalConfiguration
    : IEntityTypeConfiguration<VerifiedFileRenameJournal>
{
    public void Configure(EntityTypeBuilder<VerifiedFileRenameJournal> builder)
    {
        builder.ToTable("VerifiedFileRenameJournals");
        builder.HasKey(journal => journal.OperationId);
        builder.Property(journal => journal.SourcePath)
            .IsRequired()
            .HasMaxLength(4096);
        builder.Property(journal => journal.DestinationPath)
            .IsRequired()
            .HasMaxLength(4096);
        builder.Property(journal => journal.StagingPath)
            .IsRequired()
            .HasMaxLength(4096);
        builder.Property(journal => journal.RetirementPath)
            .IsRequired()
            .HasMaxLength(4096);
        builder.Property(journal => journal.SourceSha256)
            .IsRequired()
            .HasMaxLength(64);
        builder.Property(journal => journal.ExpectedBatchManifestSha256)
            .IsRequired()
            .HasMaxLength(64);
        builder.Property(journal => journal.Error)
            .HasMaxLength(2048);
        builder.HasIndex(journal => journal.BatchId);
        builder.HasIndex(journal => journal.AudiobookId);
        builder.HasIndex(journal => journal.State);
    }
}
