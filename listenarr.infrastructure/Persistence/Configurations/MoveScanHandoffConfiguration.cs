using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class MoveScanHandoffConfiguration : IEntityTypeConfiguration<MoveScanHandoff>
{
    public void Configure(EntityTypeBuilder<MoveScanHandoff> builder)
    {
        builder.ToTable("MoveScanHandoffs");
        builder.Property(handoff => handoff.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(handoff => handoff.LeaseOwner).HasMaxLength(200);
        builder.Property(handoff => handoff.TargetPath).HasMaxLength(2000);
        builder.Property(handoff => handoff.LastError).HasMaxLength(4000);
        builder.HasIndex(handoff => handoff.MoveJobId).IsUnique();
        builder.HasIndex(handoff => new
        {
            handoff.Status,
            handoff.NextAttemptAt,
            handoff.LeaseExpiresAt
        });
        builder.HasOne(handoff => handoff.MoveJob)
            .WithOne(job => job.ScanHandoff)
            .HasForeignKey<MoveScanHandoff>(handoff => handoff.MoveJobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MoveJobCreatedDirectoryConfiguration : IEntityTypeConfiguration<MoveJobCreatedDirectory>
{
    public void Configure(EntityTypeBuilder<MoveJobCreatedDirectory> builder)
    {
        builder.ToTable("MoveJobCreatedDirectories");
        builder.Property(directory => directory.Path).HasMaxLength(2000);
        builder.Property(directory => directory.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(directory => directory.DirectoryObjectIdentity).HasMaxLength(512);
        builder.HasIndex(directory => new { directory.MoveJobId, directory.Path }).IsUnique();
        builder.HasOne(directory => directory.MoveJob)
            .WithMany(job => job.CreatedDirectories)
            .HasForeignKey(directory => directory.MoveJobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
