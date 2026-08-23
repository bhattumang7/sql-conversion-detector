using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Common;

namespace SilentScan.Core.Catalog;

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
