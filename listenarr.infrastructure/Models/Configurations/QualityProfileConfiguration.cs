using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence.Converters;

namespace Listenarr.Infrastructure.Models.Configurations
{
    public class QualityProfileConfiguration : IEntityTypeConfiguration<QualityProfile>
    {
        public void Configure(EntityTypeBuilder<QualityProfile> builder)
        {
            builder.HasKey(q => q.Id);

            // Serialize Qualities list into a JSON TEXT column to avoid creating a separate entity type.
            var converter = new JsonValueConverter<List<QualityDefinition>>();
            var comparer = JsonValueComparer.Create<List<QualityDefinition>>();

            var prop = builder.Property(q => q.Qualities)
                .HasConversion(converter)
                .HasColumnName("Qualities")
                .HasColumnType("TEXT")
                .IsRequired();

            prop.Metadata.SetValueComparer(comparer);

            // For other string-list properties used across the profile, prefer JSON mapping if stored as TEXT.
            var preferredFormatsConverter = new JsonValueConverter<List<string>>();
            var preferredFormatsComparer = JsonValueComparer.Create<List<string>>();
            var pfProp = builder.Property(q => q.PreferredFormats)
                .HasConversion(preferredFormatsConverter)
                .HasColumnName("PreferredFormats")
                .HasColumnType("TEXT")
                .IsRequired();

            pfProp.Metadata.SetValueComparer(preferredFormatsComparer);

            var preferredLanguagesConverter = new JsonValueConverter<List<string>>();
            var preferredLanguagesComparer = JsonValueComparer.Create<List<string>>();
            var plProp = builder.Property(q => q.PreferredLanguages)
                .HasConversion(preferredLanguagesConverter)
                .HasColumnName("PreferredLanguages")
                .HasColumnType("TEXT")
                .IsRequired();

            plProp.Metadata.SetValueComparer(preferredLanguagesComparer);

            var mustContainConverter = new JsonValueConverter<List<string>>();
            var mustContainComparer = JsonValueComparer.Create<List<string>>();
            var mustContainProp = builder.Property(q => q.MustContain)
                .HasConversion(mustContainConverter)
                .HasColumnName("MustContain")
                .HasColumnType("TEXT")
                .IsRequired();

            mustContainProp.Metadata.SetValueComparer(mustContainComparer);

            var mustNotContainConverter = new JsonValueConverter<List<string>>();
            var mustNotContainComparer = JsonValueComparer.Create<List<string>>();
            var mustNotContainProp = builder.Property(q => q.MustNotContain)
                .HasConversion(mustNotContainConverter)
                .HasColumnName("MustNotContain")
                .HasColumnType("TEXT")
                .IsRequired();

            mustNotContainProp.Metadata.SetValueComparer(mustNotContainComparer);

            // Configure CustomGroupNames as JSON dictionary (optional field)
            var customGroupNamesConverter = new JsonValueConverter<Dictionary<string, string>?>();
            var customGroupNamesComparer = JsonValueComparer.Create<Dictionary<string, string>?>();
            var customGroupNamesProp = builder.Property(q => q.CustomGroupNames)
                .HasConversion(customGroupNamesConverter)
                .HasColumnName("CustomGroupNames")
                .HasColumnType("TEXT")
                .IsRequired(false);

            customGroupNamesProp.Metadata.SetValueComparer(customGroupNamesComparer);
        }
    }
}
