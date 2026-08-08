using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class LibraryDirectoryOwnershipConfiguration
    : IEntityTypeConfiguration<LibraryDirectoryOwnership>
{
    public void Configure(EntityTypeBuilder<LibraryDirectoryOwnership> builder)
    {
        builder.ToTable("LibraryDirectoryOwnerships");
        builder.Property(ownership => ownership.Path).HasMaxLength(2000);
        builder.Property(ownership => ownership.CanonicalPath).HasMaxLength(4096);
        builder.Property(ownership => ownership.PathSyntax).HasConversion<string>().HasMaxLength(16);
        builder.Property(ownership => ownership.PathCaseSensitivity).HasConversion<string>().HasMaxLength(16);
        builder.Property(ownership => ownership.PathCaseSensitivityMode).HasConversion<string>().HasMaxLength(16);
        builder.Property(ownership => ownership.PathIdentityBoundary).HasMaxLength(4096);
        builder.Property(ownership => ownership.PathIdentityLookupKey).HasMaxLength(160);
        builder.Property(ownership => ownership.PathOwnershipKey).HasMaxLength(160);
        builder.Property(ownership => ownership.OwnershipToken).HasMaxLength(64);
        builder.Property(ownership => ownership.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(ownership => ownership.CreationWorkflow).HasMaxLength(64);
        builder.Property(ownership => ownership.DirectoryObjectIdentity).HasMaxLength(256);
        builder.Property(ownership => ownership.DirectoryObjectIdentityUnavailableReason).HasMaxLength(1024);
        builder.Property(ownership => ownership.StateReason).HasMaxLength(1024);

        builder.HasIndex(ownership => ownership.PathIdentityLookupKey);
        builder.HasIndex(ownership => ownership.OwnershipToken).IsUnique();
        builder.HasIndex(ownership => ownership.ManagedRootFolderId);
        builder.HasOne<RootFolder>()
            .WithMany()
            .HasForeignKey(ownership => ownership.ManagedRootFolderId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(ownership => ownership.PathOwnershipKey)
            .IsUnique()
            .HasFilter("\"PathOwnershipKey\" IS NOT NULL");
        builder.HasIndex(ownership => new
        {
            ownership.CreationOperationId,
            ownership.State
        });
    }
}
