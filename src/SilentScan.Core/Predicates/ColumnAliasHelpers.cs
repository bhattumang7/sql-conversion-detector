using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

internal static class ColumnAliasHelpers
{
    public static string? ColumnNameIfQualifiedByAlias(ScalarExpression expression, string alias)
    {
        if (expression is not ColumnReferenceExpression columnRef)
        {
            return null;
        }

        var identifiers = columnRef.MultiPartIdentifier.Identifiers;
        return identifiers.Count >= 2 && string.Equals(identifiers[^2].Value, alias, StringComparison.OrdinalIgnoreCase)
            ? identifiers[^1].Value
            : null;
    }

    public sealed class RawColumnReferenceCollector : TSqlFragmentVisitor
    {
        public List<ColumnReferenceExpression> References { get; } = [];

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            References.Add(node);
            base.ExplicitVisit(node);
        }
    }
}
