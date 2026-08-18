using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass over every DEFAULT constraint (<see cref="DefaultNullableConstraintFinding"/>
/// for the full precision story and oracle evidence) - runs in both file mode and live mode, since
/// <see cref="DatabaseCatalog.SchemaExpressions"/> is populated by both <see
/// cref="SchemaExpressionCollector"/> (file) and <c>LiveCatalogReader</c> (live), unlike <see
/// cref="CheckConstraintScanner"/>'s live-only <see cref="CatalogCheckConstraint.DefinitionText"/>
/// source.
/// </summary>
public static class DefaultNullableConstraintScanner
{
    public static IReadOnlyList<DefaultNullableConstraintFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<DefaultNullableConstraintFinding>();

        foreach (var expression in catalog.SchemaExpressions)
        {
            if (expression.Kind != SchemaDependencyKind.DefaultConstraint || expression.ColumnName is not { } columnName)
            {
                continue;
            }

            var column = catalog.Find(expression.TableQualifiedName)?.FindColumn(columnName);
            if (column is not { IsNullable: true })
            {
                continue;
            }

            findings.Add(new DefaultNullableConstraintFinding(
                expression.TableQualifiedName, columnName, expression.DefinitionText, expression.SourcePath, expression.Line));
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }
}
