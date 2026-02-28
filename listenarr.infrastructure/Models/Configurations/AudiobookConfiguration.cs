using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Listenarr.Domain.Models;

namespace Listenarr.Infrastructure.Models.Configurations
{
    /// <summary>
    /// EF mapping for Audiobook entity extracted from ListenArrDbContext.
    /// Keeps conversions / comparers and relationships colocated for easier testing & reuse.
    /// </summary>
    public class AudiobookConfiguration : IEntityTypeConfiguration<Audiobook>
    {
        private static ValueComparer<List<string>> StringListComparer() =>
            new ValueComparer<List<string>>(
                (c1, c2) => (c1 ?? new List<string>()).SequenceEqual(c2 ?? new List<string>()),
                c => (c ?? new List<string>()).Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c == null ? new List<string>() : c.ToList()
            );

        public void Configure(EntityTypeBuilder<Audiobook> builder)
        {
            // Map list-of-string properties as JSON TEXT columns so EF will properly
            // deserialize the JSON arrays that migrations now produce (e.g. ["Name"]).
            var authorsConverter = (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<string>?, string>) new Listenarr.Infrastructure.Persistence.Converters.JsonValueConverter<List<string>?>();
            var authorsComparer = Listenarr.Infrastructure.Persistence.Converters.JsonValueComparer.Create<List<string>?>();
            var authorsProp = builder.Property(e => e.Authors)
                .HasConversion(authorsConverter)
                .HasColumnType("TEXT");
            authorsProp.Metadata.SetValueComparer(authorsComparer);

            var genresConverter = (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<string>?, string>) new Listenarr.Infrastructure.Persistence.Converters.JsonValueConverter<List<string>?>();
            var genresComparer = Listenarr.Infrastructure.Persistence.Converters.JsonValueComparer.Create<List<string>?>();
            var genresProp = builder.Property(e => e.Genres)
                .HasConversion(genresConverter)
                .HasColumnType("TEXT");
            genresProp.Metadata.SetValueComparer(genresComparer);

            var tagsConverter = (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<string>?, string>) new Listenarr.Infrastructure.Persistence.Converters.JsonValueConverter<List<string>?>();
            var tagsComparer = Listenarr.Infrastructure.Persistence.Converters.JsonValueComparer.Create<List<string>?>();
            var tagsProp = builder.Property(e => e.Tags)
                .HasConversion(tagsConverter)
                .HasColumnType("TEXT");
            tagsProp.Metadata.SetValueComparer(tagsComparer);

            var narratorsConverter = (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<string>?, string>) new Listenarr.Infrastructure.Persistence.Converters.JsonValueConverter<List<string>?>();
            var narratorsComparer = Listenarr.Infrastructure.Persistence.Converters.JsonValueComparer.Create<List<string>?>();
            var narratorsProp = builder.Property(e => e.Narrators)
                .HasConversion(narratorsConverter)
                .HasColumnType("TEXT");
            narratorsProp.Metadata.SetValueComparer(narratorsComparer);

            var isbnConverter = (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<string>?, string>) new Listenarr.Infrastructure.Persistence.Converters.JsonValueConverter<List<string>?>();
            var isbnComparer = Listenarr.Infrastructure.Persistence.Converters.JsonValueComparer.Create<List<string>?>();
            var isbnProp = builder.Property(e => e.Isbn)
                .HasConversion(isbnConverter)
                .HasColumnType("TEXT");
            isbnProp.Metadata.SetValueComparer(isbnComparer);

            // Author ASINs (resolve/cached author images rely on stored ASINs)
            var authorAsinsConverter = (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<List<string>?, string>) new Listenarr.Infrastructure.Persistence.Converters.JsonValueConverter<List<string>?>();
            var authorAsinsComparer = Listenarr.Infrastructure.Persistence.Converters.JsonValueComparer.Create<List<string>?>();
            var authorAsinsProp = builder.Property(e => e.AuthorAsins)
                .HasConversion(authorAsinsConverter)
                .HasColumnType("TEXT");
            authorAsinsProp.Metadata.SetValueComparer(authorAsinsComparer);

            // One-to-many: Audiobook -> AudiobookFiles
            builder.HasMany(a => a.Files)
                .WithOne(f => f.Audiobook)
                .HasForeignKey(f => f.AudiobookId)
                .OnDelete(DeleteBehavior.Cascade);

            // One-to-many: Audiobook -> ExternalIdentifiers
            builder.HasMany(a => a.ExternalIdentifiers)
                .WithOne()
                .HasForeignKey(i => i.AudiobookId)
                .OnDelete(DeleteBehavior.Cascade);

            // Performance indexes commonly used by queries
            builder.HasIndex(a => a.Monitored);
            builder.HasIndex(a => a.LastSearchTime);
        }
    }
}
