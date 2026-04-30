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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence.Converters;

namespace Listenarr.Infrastructure.Models.Configurations
{
    public class ApiConfigurationConfiguration : IEntityTypeConfiguration<ApiConfiguration>
    {
        public void Configure(EntityTypeBuilder<ApiConfiguration> builder)
        {
            builder.HasKey(a => a.Id);

            // Ensure JSON backing column type when using conversion (only configure the converted
            // property below so EF doesn't try to map both the backing string and the converted
            // property as separate columns which can cause duplicate '...Json1' columns to appear).

            // Centralized JSON converter/comparer — expression-tree safe.
            var converter = new JsonValueConverter<Dictionary<string, string>>();
            var comparer = JsonValueComparer.Create<Dictionary<string, string>>();

            // Ensure EF doesn't separately map the raw backing JSON string property -
            // only the converted property will be mapped to the column name.
            builder.Ignore(a => a.HeadersJson);
            builder.Ignore(a => a.ParametersJson);

            var headersProp = builder.Property(a => a.Headers)
                .HasConversion(converter)
                .HasColumnName(nameof(ApiConfiguration.HeadersJson))
                .HasColumnType("TEXT");

            headersProp.Metadata.SetValueComparer(comparer);

            var parametersProp = builder.Property(a => a.Parameters)
                .HasConversion(converter)
                .HasColumnName(nameof(ApiConfiguration.ParametersJson))
                .HasColumnType("TEXT");

            parametersProp.Metadata.SetValueComparer(comparer);
        }
    }
}
