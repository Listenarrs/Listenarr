using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

internal sealed class WeakStorageScanCandidateConfiguration
    : IEntityTypeConfiguration<WeakStorageScanCandidate>
{
    public void Configure(EntityTypeBuilder<WeakStorageScanCandidate> builder)
    {
        builder.ToTable("WeakStorageScanCandidates");
        builder.HasKey(candidate => candidate.Id);
        builder.Property(candidate => candidate.ExpectedStoredPath)
            .IsRequired()
            .HasMaxLength(4096);
        builder.Property(candidate => candidate.ExpectedResolvedPath)
            .IsRequired()
            .HasMaxLength(4096);
        builder.Property(candidate => candidate.ExpectedPhysicalObjectIdentity)
            .HasMaxLength(512);
        builder.HasIndex(candidate => new
        {
            candidate.AudiobookId,
            candidate.ConfirmedAt,
            candidate.ExpiresAt
        });
        builder.HasIndex(candidate => candidate.ScanToken);
    }
}
