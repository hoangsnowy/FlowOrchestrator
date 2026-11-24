using FlowOrchestrator.StorageMigration.Internal;

namespace FlowOrchestrator.StorageMigration.Schema;

/// <summary>Compares a database's schema with <see cref="StorageCatalog"/>.</summary>
internal static class SchemaChecks
{
    /// <summary>
    /// Lists what <paramref name="schema"/> lacks: a whole table as <c>table</c>, a column as <c>table.column</c>,
    /// spelled as on <paramref name="provider"/>.
    /// </summary>
    public static List<string> FindMissing(SchemaSnapshot schema, FlowStorageProvider provider, IEnumerable<StorageTable> tables)
    {
        var missing = new List<string>();
        foreach (var table in tables)
        {
            var name = table.NameOn(provider);
            var columns = schema.ColumnsOf(name);
            if (columns is null)
            {
                missing.Add(name);
                continue;
            }

            missing.AddRange(table.Columns
                .Select(column => column.NameOn(provider))
                .Where(columnName => !columns.Contains(columnName))
                .Select(columnName => name + "." + columnName));
        }

        return missing;
    }

    /// <summary>
    /// Lists the columns of the existing <paramref name="tables"/> that the catalog does not map (and that are not
    /// deliberately ignored), as <c>table.column</c>.
    /// </summary>
    public static List<string> FindUnknownColumns(SchemaSnapshot schema, FlowStorageProvider provider, IEnumerable<StorageTable> tables)
    {
        var unknown = new List<string>();
        foreach (var table in tables)
        {
            var name = table.NameOn(provider);
            var columns = schema.ColumnsOf(name);
            if (columns is null)
                continue;

            unknown.AddRange(columns
                .Where(existing => !IsKnown(table, provider, existing))
                .Select(existing => name + "." + existing));
        }

        unknown.Sort(StringComparer.Ordinal);
        return unknown;
    }

    private static bool IsKnown(StorageTable table, FlowStorageProvider provider, string column)
    {
        foreach (var mapped in table.Columns)
        {
            if (string.Equals(mapped.NameOn(provider), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (provider == FlowStorageProvider.SqlServer)
        {
            foreach (var ignored in table.IgnoredSqlColumns)
            {
                if (string.Equals(ignored, column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
