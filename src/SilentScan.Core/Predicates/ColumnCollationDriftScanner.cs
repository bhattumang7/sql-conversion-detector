using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md Tier 1): no AST walking, no predicate site
/// needed - runs directly over the already-built <see cref="DatabaseCatalog"/> and flags every
/// string-family column whose own collation differs from the relevant baseline (the database's
/// default collation for an ordinary table, tempdb's effective collation for a temp
/// table/table variable). Never guesses: a column whose collation didn't resolve, or a catalog
/// whose own baseline collation is unknown, is silently skipped rather than reported either way.
/// </summary>
public static class ColumnCollationDriftScanner
{
    public static IReadOnlyList<ColumnCollationDriftFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<ColumnCollationDriftFinding>();

        foreach (var table in catalog.Tables)
        {
            var isTempObject = table.Kind is CatalogTableKind.TemporaryTable or CatalogTableKind.TableVariable;
            var baseline = isTempObject ? catalog.EffectiveTempdbCollation : catalog.DefaultCollation;
            if (baseline is null)
            {
                continue;
            }

            foreach (var column in table.Columns)
            {
                if (column.Type is not { IsStringFamily: true, Collation: { } columnCollation })
                {
                    continue;
                }

                if (!string.Equals(columnCollation.Name, baseline.Name, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new ColumnCollationDriftFinding(
                        table.QualifiedName, column.Name, columnCollation.Name, baseline.Name,
                        isTempObject, table.SourcePath, table.SourceLine));
                }
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
