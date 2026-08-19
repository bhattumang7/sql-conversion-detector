using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Two syntax-only helpers - no catalog or scope resolution, just "is this column reference
/// qualified by this alias" - that survived the deletion of <c>DirectBaseTableResolver</c> (Phase
/// 1.5 "one binder": every scanner that needed catalog-bound base-table resolution now goes
/// through <see cref="Lineage.FromScopeResolver"/> instead). Kept as their own neutral home rather
/// than left inside a type whose entire reason for existing (the catalog-bypass resolution) is
/// gone.
/// </summary>
internal static class ColumnAliasHelpers
{
    /// <summary>The last-identifier-matches-alias rule shared by every scanner that resolves a column reference back to a join alias without going through the full scope-chain machinery.</summary>
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

    /// <summary>Collects every <see cref="ColumnReferenceExpression"/> reachable from a fragment, unresolved - used only to then test each one against <see cref="ColumnNameIfQualifiedByAlias"/>.</summary>
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
