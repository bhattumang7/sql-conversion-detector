using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

public sealed class LineageParityChecker
{
    private readonly ColumnCatalogReader _reader;

    public LineageParityChecker(SqlServerOptions options)
    {
        _reader = new ColumnCatalogReader(options);
    }

    public async Task<IReadOnlyList<LineageParityMismatch>> CheckAsync(
        string database, LineageCatalog lineage, CancellationToken cancellationToken = default)
    {
        var mismatches = new List<LineageParityMismatch>();

        foreach (var (qualifiedName, relation) in lineage.AllRelations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (qualifiedName is null || lineage.CyclicViews.Contains(qualifiedName))
            {
                continue;
            }

            IReadOnlyList<CatalogColumnInfo> oracleColumns;
            try
            {
                oracleColumns = await _reader.ReadColumnsAsync(database, qualifiedName, cancellationToken);
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {

                continue;
            }

            if (oracleColumns.Count == 0)
            {
                continue;
            }

            foreach (var column in relation.Columns)
            {
                var type = ColumnProvenanceAnalysis.TryGetScalarType(column.Provenance);
                if (type is null)
                {
                    continue;
                }

                var oracleColumn = oracleColumns.FirstOrDefault(c => string.Equals(c.ColumnName, column.Name, StringComparison.OrdinalIgnoreCase));
                if (oracleColumn is null)
                {
                    continue;
                }

                CheckColumn(qualifiedName, column.Name, type, oracleColumn, mismatches);
            }
        }

        return mismatches;
    }

    private static void CheckColumn(string qualifiedName, string columnName, SqlType type, CatalogColumnInfo oracleColumn, List<LineageParityMismatch> mismatches)
    {
        if (!string.Equals(oracleColumn.TypeName, CategoryTypeName(type.Category), StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new LineageParityMismatch(qualifiedName, columnName, "category", type.Category.ToString(), oracleColumn.TypeName));
            return;
        }

        if (type.IsStringFamily && type.Collation is not null
            && !string.Equals(type.Collation.Name, oracleColumn.CollationName, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new LineageParityMismatch(qualifiedName, columnName, "collation", type.Collation.Name, oracleColumn.CollationName ?? "(null)"));
        }

        if (IsStringOrBinaryFamily(type.Category) && !LengthMatches(type, oracleColumn, out var inferredLength, out var actualLength))
        {
            mismatches.Add(new LineageParityMismatch(qualifiedName, columnName, "length", inferredLength, actualLength));
        }

        if (type.Precision is { } precision && precision != oracleColumn.Precision)
        {
            mismatches.Add(new LineageParityMismatch(qualifiedName, columnName, "precision", precision.ToString(System.Globalization.CultureInfo.InvariantCulture), oracleColumn.Precision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (type.Scale is { } scale && scale != oracleColumn.Scale)
        {
            mismatches.Add(new LineageParityMismatch(qualifiedName, columnName, "scale", scale.ToString(System.Globalization.CultureInfo.InvariantCulture), oracleColumn.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    private static string CategoryTypeName(SqlTypeCategory category) =>
        category == SqlTypeCategory.SqlVariant ? "sql_variant" : category.ToString();

    private static bool IsStringOrBinaryFamily(SqlTypeCategory category) =>
        category is SqlTypeCategory.Char or SqlTypeCategory.VarChar or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar
            or SqlTypeCategory.Binary or SqlTypeCategory.VarBinary;

    private static bool LengthMatches(SqlType type, CatalogColumnInfo oracleColumn, out string inferredLength, out string actualLength)
    {
        inferredLength = string.Empty;
        actualLength = string.Empty;

        if (type.IsMax)
        {
            if (oracleColumn.MaxLength == -1)
            {
                return true;
            }

            inferredLength = "MAX";
            actualLength = oracleColumn.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        if (type.Length is not { } length)
        {
            return true;
        }

        var expectedMaxLength = type.IsUnicodeString ? length * 2 : length;
        if (oracleColumn.MaxLength == expectedMaxLength)
        {
            return true;
        }

        inferredLength = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        actualLength = (type.IsUnicodeString ? oracleColumn.MaxLength / 2 : oracleColumn.MaxLength).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return false;
    }
}

public sealed record LineageParityMismatch(
    string QualifiedViewName,
    string ColumnName,
    string Facet,
    string InferredValue,
    string ActualValue);
