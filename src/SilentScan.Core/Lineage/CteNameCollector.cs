using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Lineage;

internal static class CteNameCollector
{
    public static IReadOnlySet<string> Collect(TSqlFragment fragment, StringComparer? identifierComparer = null)
    {
        var collector = new Collector(identifierComparer ?? StringComparer.OrdinalIgnoreCase);
        fragment.Accept(collector);
        return collector.Names;
    }

    private sealed class Collector(StringComparer identifierComparer) : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(identifierComparer);

        public override void Visit(CommonTableExpression node) => Names.Add(node.ExpressionName.Value);
    }
}
