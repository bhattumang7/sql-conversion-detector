using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Lineage;

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
