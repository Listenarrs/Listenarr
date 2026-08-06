using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class FileMutationJournalConfiguration :
    IEntityTypeConfiguration<FileMutationJournal>
{
    public void Configure(EntityTypeBuilder<FileMutationJournal> builder)
    {
        builder.ToTable("FileMutationJournals");
        builder.Property(journal => journal.ProtocolVersion)
            .HasDefaultValue(FileMutationProtocol.MarkerlessDatabaseState);
        builder.Property(journal => journal.Action)
            .HasConversion<string>()
            .HasMaxLength(24);
        builder.Property(journal => journal.State)
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(journal => journal.SourcePath).HasMaxLength(4096);
        builder.Property(journal => journal.DestinationPath).HasMaxLength(4096);
        builder.Property(journal => journal.SourcePhysicalObjectIdentity)
            .HasMaxLength(512);
        builder.Property(journal => journal.TargetPhysicalObjectIdentity)
            .HasMaxLength(512);
        builder.Property(journal => journal.SourceSha256).HasMaxLength(64);
        builder.Property(journal => journal.Error).HasMaxLength(2048);
        builder.HasIndex(journal => journal.State);
        builder.HasIndex(journal => journal.UpdatedAt);
    }
}
