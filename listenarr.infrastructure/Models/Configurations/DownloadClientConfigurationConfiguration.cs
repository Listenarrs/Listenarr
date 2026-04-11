using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Listenarr.Domain.Models.Configurations;

namespace Listenarr.Infrastructure.Models.Configurations
{
    public class DownloadClientConfigurationConfiguration : IEntityTypeConfiguration<DownloadClientConfiguration>
    {
        public void Configure(EntityTypeBuilder<DownloadClientConfiguration> builder)
        {
            builder.HasKey(d => d.Id);

            // Do not map the raw SettingsJson backing property separately. The converted
            // Settings property will be mapped to the same column name below. Mapping
            // both separately can result in duplicate column names (SettingsJson1).

            // Converter for Dictionary<string, object> -> JSON string
            var converter = new ValueConverter<Dictionary<string, object>, string>(
                dict => SerializeSettings(dict),
                json => DeserializeSettings(json));

            // Comparer uses serialized JSON for equality and cloning
            var comparer = new ValueComparer<Dictionary<string, object>>(
                (a, b) => SerializeSettings(a) == SerializeSettings(b),
                v => SerializeSettings(v).GetHashCode(),
                v => DeserializeSettings(SerializeSettings(v)));

            // Ensure EF doesn't separately map the raw backing JSON string property -
            // only the converted property will be mapped to the column name.
            builder.Ignore(d => d.SettingsJson);

            var settingsProp = builder.Property(d => d.Settings)
                .HasConversion(converter)
                .HasColumnName(nameof(DownloadClientConfiguration.SettingsJson))
                .HasColumnType("TEXT");

            settingsProp.Metadata.SetValueComparer(comparer);
        }

        private static string SerializeSettings(Dictionary<string, object>? dict) =>
            JsonSerializer.Serialize(dict ?? new Dictionary<string, object>());

        private static Dictionary<string, object> DeserializeSettings(string json) =>
            string.IsNullOrWhiteSpace(json)
                ? new Dictionary<string, object>()
                : JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
    }
}
