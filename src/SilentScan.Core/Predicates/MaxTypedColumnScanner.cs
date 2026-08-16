using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters"
/// #3) - no AST walking, mirrors <see cref="ColumnCollationDriftScanner"/>'s own shape: every
/// MAX-typed string/binary column is flagged once, regardless of whether any scanned query
/// actually touches it - a MAX-typed column structurally can never be an index key (the engine
/// itself refuses to create one), so this is a stable fact about the schema, not usage-dependent.
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
                if (column.Type is not { IsMax: true } type)
                {
                    continue;
                }

                findings.Add(new MaxTypedColumnFinding(
                    table.QualifiedName, column.Name, type.ToString(), table.SourcePath, table.SourceLine));
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
