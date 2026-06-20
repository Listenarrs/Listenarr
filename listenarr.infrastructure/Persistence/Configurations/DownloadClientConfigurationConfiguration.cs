/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Listenarr.Infrastructure.Persistence.Configurations
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
