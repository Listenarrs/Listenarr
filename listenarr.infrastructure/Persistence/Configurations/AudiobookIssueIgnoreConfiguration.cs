using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Listenarr.Infrastructure.Persistence.Configurations;

public sealed class AudiobookIssueIgnoreConfiguration :
    IEntityTypeConfiguration<AudiobookIssueIgnore>
{
    public void Configure(EntityTypeBuilder<AudiobookIssueIgnore> builder)
    {
        builder.ToTable("AudiobookIssueIgnores");
        builder.HasKey(ignore => ignore.AudiobookId);
        builder.HasOne<Audiobook>()
            .WithMany()
            .HasForeignKey(ignore => ignore.AudiobookId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
