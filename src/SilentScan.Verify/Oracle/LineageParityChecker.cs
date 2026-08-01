using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// The environment parity gate CLAUDE.md's Verify workflow calls for but which, before this,
/// only ran inside hand-picked unit tests: after a repo's DDL deploys, diff every view's
/// statically-inferred column type against sys.columns and fail loudly on any mismatch ("any
/// mismatch is a P0 lineage bug"), rather than trusting the static classifier's types silently
/// for the rest of the pipeline. Covers every provenance kind <see
/// cref="ColumnProvenanceAnalysis.TryGetScalarType"/> can resolve a type for - a direct
/// BaseColumn passthrough, but also a CAST/CONVERT's explicit target type, Pass 2's own
/// inferred Expression type, a multi-statement TVF's Declared column, and a Union whose
/// branches all agreed - not only the passthrough case. Length/precision/scale are diffed
/// alongside category/collation: a category match with the wrong length is exactly the kind of
/// wrong-but-plausible answer this gate exists to catch (an audit finding - the previous
/// category+collation-only diff would pass a `varchar(20)` column statically typed as
/// `varchar(50)` clean).
/// </summary>
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
                // Not every resolved relation is a real deployed view (derived tables, MSTVFs
                // that never became a server object) - absence here is not itself a mismatch.
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
        if (!string.Equals(oracleColumn.TypeName, type.Category.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new LineageParityMismatch(qualifiedName, columnName, "category", type.Category.ToString(), oracleColumn.TypeName));
            return;
        }

        if (type.IsStringFamily && !string.Equals(type.Collation?.Name, oracleColumn.CollationName, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new LineageParityMismatch(qualifiedName, columnName, "collation", type.Collation?.Name ?? "(null)", oracleColumn.CollationName ?? "(null)"));
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

    private static bool IsStringOrBinaryFamily(SqlTypeCategory category) =>
        category is SqlTypeCategory.Char or SqlTypeCategory.VarChar or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar
            or SqlTypeCategory.Binary or SqlTypeCategory.VarBinary;

    /// <summary>
    /// sys.columns.max_length is a BYTE count, not a character count - verified directly against
    /// the real engine: an nchar/nvarchar column's max_length is always double its declared
    /// character length (Unicode is 2 bytes/char), while char/varchar/binary/varbinary's
    /// max_length equals the declared length exactly. A (max) column reports max_length = -1
    /// regardless of family. Returns true (nothing to compare) when the inferred type carries no
    /// length facet at all - not every string-family SqlType construction site sets one.
    /// </summary>
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

/// <summary>One inferred-vs-actual disagreement the environment parity gate found - CLAUDE.md: "any mismatch is a P0 lineage bug".</summary>
public sealed record LineageParityMismatch(
    string QualifiedViewName,
    string ColumnName,
    string Facet,
    string InferredValue,
    string ActualValue);
