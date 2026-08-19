using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Every CTE name declared anywhere in a fragment (via any <c>WITH ... AS (...)</c> clause
/// reachable in it), shared by every side-map builder that walks a view/TVF/UDF body looking for
/// real object references (<see cref="ViewDependencyGraph"/>, <see cref="ViewExpansionMap"/>,
/// <see cref="TvfFenceMap"/>, <see cref="ScalarUdfMap"/>). A CTE name is never schema-qualified,
/// so an unqualified table reference matching one can never mean a real catalog object instead -
/// the same shadowing rule <see cref="FromScopeResolver"/> already applies during actual column
/// resolution, extended here to the coarser "does this body reference object X at all" maps,
/// which previously had no CTE awareness and could record a false self-edge/dependency/fence-
/// inheritance/UDF-carrier flag whenever a CTE happened to share a real object's name.
/// </summary>
internal static class CteNameCollector
{
    public static IReadOnlySet<string> Collect(TSqlFragment fragment)
    {
        var collector = new Collector();
        fragment.Accept(collector);
        return collector.Names;
    }

    private sealed class Collector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(CommonTableExpression node) => Names.Add(node.ExpressionName.Value);
    }
}
