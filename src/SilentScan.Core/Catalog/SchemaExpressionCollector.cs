using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Catalog;

/// <summary>
/// File-mode capture of a real table's own computed-column/DEFAULT/CHECK definitions, as TEXT
/// (<see cref="SchemaExpressionReference"/>) - the file-mode mirror of what live mode reads
/// straight from <c>sys.computed_columns</c>/<c>sys.default_constraints</c>/
/// <c>sys.check_constraints</c>. Never called for a #temp table, table variable, or TVF's
/// <c>RETURNS @t TABLE(...)</c> shape - those aren't schema (docs/detection-checklist.md Tier 1
/// #1's "computed column, DEFAULT, or CHECK constraint" sub-item is about real persistent tables
/// whose cost is paid by every query touching them, not a query-local scratch shape).
/// </summary>
internal static class SchemaExpressionCollector
{
    public static IEnumerable<SchemaExpressionReference> Collect(TableDefinition definition, string tableQualifiedName, string sourcePath)
    {
        foreach (var column in definition.ColumnDefinitions)
        {
            if (column.ComputedColumnExpression is { } computed)
            {
                yield return new SchemaExpressionReference(
                    SchemaDependencyKind.ComputedColumn, tableQualifiedName, column.ColumnIdentifier.Value,
                    FragmentTextRenderer.Render(computed), sourcePath, computed.StartLine);
            }

            if (column.DefaultConstraint is { Expression: { } defaultExpression } defaultConstraint)
            {
                yield return new SchemaExpressionReference(
                    SchemaDependencyKind.DefaultConstraint, tableQualifiedName, column.ColumnIdentifier.Value,
                    FragmentTextRenderer.Render(defaultExpression), sourcePath, defaultConstraint.StartLine);
            }

            foreach (var reference in CollectCheckConstraints(column.Constraints, tableQualifiedName, column.ColumnIdentifier.Value, sourcePath))
            {
                yield return reference;
            }
        }

        foreach (var reference in CollectCheckConstraints(definition.TableConstraints, tableQualifiedName, columnName: null, sourcePath))
        {
            yield return reference;
        }
    }

    private static IEnumerable<SchemaExpressionReference> CollectCheckConstraints(
        IList<ConstraintDefinition> constraints, string tableQualifiedName, string? columnName, string sourcePath)
    {
        foreach (var constraint in constraints)
        {
            if (constraint is CheckConstraintDefinition { CheckCondition: { } condition } check)
            {
                yield return new SchemaExpressionReference(
                    SchemaDependencyKind.CheckConstraint, tableQualifiedName, columnName,
                    FragmentTextRenderer.Render(condition), sourcePath, check.StartLine);
            }
        }
    }
}
