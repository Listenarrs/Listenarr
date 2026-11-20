using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.Migrations;

/// <summary>
/// Writes rows into a database migrated only as far as a released migration frontier.
///
/// The current model can map columns that migrations after the frontier add, and EF names every
/// mapped column in its INSERT, so writing current entities into the released schema fails on the
/// first such column. This context's model leaves out each property whose table the frontier's
/// target model has but whose column it does not, so rows are stored as that release would have
/// stored them and the upgrade under test fills in the rest. Tables the frontier did not have at
/// all are left as they are; nothing written before the upgrade can use them.
/// </summary>
internal static class ReleasedSchemaWriteContext
{
    public static ListenArrDbContext Create<TFrontier>(string connectionString)
        where TFrontier : Migration, new() =>
        new(new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connectionString)
            .ReplaceService<IModelCustomizer, FrontierModelCustomizer<TFrontier>>()
            .Options);

    private sealed class FrontierModelCustomizer<TFrontier>(ModelCustomizerDependencies dependencies)
        : RelationalModelCustomizer(dependencies)
        where TFrontier : Migration, new()
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            var releasedColumns = ColumnsByTable(new TFrontier().TargetModel!);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                .Where(entityType => !entityType.IsOwned())
                .ToList())
            {
                var table = entityType.GetTableName();
                if (table is null || !releasedColumns.TryGetValue(table, out var columns))
                {
                    continue;
                }

                foreach (var property in entityType.GetDeclaredProperties()
                    .Where(property => !property.IsKey()
                        && !property.IsForeignKey()
                        && !columns.Contains(property.GetColumnName()))
                    .ToList())
                {
                    // Ignore rather than remove, so an index on the property goes with it.
                    modelBuilder.Entity(entityType.ClrType).Ignore(property.Name);
                }
            }
        }

        private static Dictionary<string, HashSet<string>> ColumnsByTable(IModel model)
        {
            var columnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var entityType in model.GetEntityTypes())
            {
                var table = entityType.GetTableName();
                if (table is null)
                {
                    continue;
                }

                if (!columnsByTable.TryGetValue(table, out var columns))
                {
                    columns = new HashSet<string>(StringComparer.Ordinal);
                    columnsByTable[table] = columns;
                }

                foreach (var property in entityType.GetProperties())
                {
                    columns.Add(property.GetColumnName());
                }
            }

            return columnsByTable;
        }
    }
}
