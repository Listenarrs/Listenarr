using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Models.Configurations
{
    public class AudiobookExternalIdentifierConfiguration : IEntityTypeConfiguration<AudiobookExternalIdentifier>
    {
        public void Configure(EntityTypeBuilder<AudiobookExternalIdentifier> builder)
        {
            builder.ToTable("AudiobookExternalIdentifiers");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.Type)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            builder.Property(x => x.Source)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            builder.Property(x => x.ValueRaw)
                .HasMaxLength(512)
                .IsRequired();

            builder.Property(x => x.ValueNormalized)
                .HasMaxLength(256)
                .IsRequired();

            builder.Property(x => x.Region)
                .HasMaxLength(8);

            builder.Property(x => x.IsPrimary)
                .IsRequired();

            builder.Property(x => x.CreatedAt)
                .IsRequired();

            builder.Property(x => x.UpdatedAt)
                .IsRequired();

            builder.HasIndex(x => x.AudiobookId);
            builder.HasIndex(x => new { x.Type, x.ValueNormalized });
            builder.HasIndex(x => new { x.Type, x.ValueNormalized, x.Region });
            builder.HasIndex(x => new { x.AudiobookId, x.Type, x.IsPrimary });
        }
    }
}
