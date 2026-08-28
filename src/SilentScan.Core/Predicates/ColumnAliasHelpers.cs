using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

internal static class ColumnAliasHelpers
{
    public static string? ColumnNameIfQualifiedByAlias(ScalarExpression expression, string alias, StringComparer? identifierComparer = null)
    {
        if (expression is not ColumnReferenceExpression columnRef)
        {
            return null;
        }

        var identifiers = columnRef.MultiPartIdentifier.Identifiers;
        return identifiers.Count >= 2 && (identifierComparer ?? StringComparer.OrdinalIgnoreCase).Equals(identifiers[^2].Value, alias)
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
