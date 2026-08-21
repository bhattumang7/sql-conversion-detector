using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters"
/// #3) - no AST walking, mirrors <see cref="ColumnCollationDriftScanner"/>'s own shape: every
/// MAX-typed string/binary column, and every legacy large-object (TEXT/NTEXT/IMAGE) column, is
/// flagged once, regardless of whether any scanned query actually touches it - both kinds
/// structurally can never be an index key (the engine itself refuses to create one; TEXT/NTEXT/
/// IMAGE go further and refuse even an INCLUDE column, see <see
/// cref="NonIndexableColumnFindingKind"/>), so this is a stable fact about the schema, not
/// usage-dependent.
/// </summary>
public static class MaxTypedColumnScanner
{
    public static IReadOnlyList<MaxTypedColumnFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<MaxTypedColumnFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.Type is not { } type)
                {
                    continue;
                }

                NonIndexableColumnFindingKind kind;

                if (type.IsMax)
                {
                    kind = NonIndexableColumnFindingKind.MaxLength;
                }
                else if (type.Category is SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image)
                {
                    kind = NonIndexableColumnFindingKind.LegacyLargeObject;
                }
                else
                {
                    continue;
                }

                findings.Add(new MaxTypedColumnFinding(
                    table.QualifiedName, column.Name, type.ToString(), table.SourcePath, table.SourceLine, kind));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }
}
