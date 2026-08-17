using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>docs/detection-checklist.md "Second OSS/commercial sweep": TRUNCATE TABLE inside a
/// TRY block whose CATCH swallows the error. Fully syntax-only.</summary>
public static class TruncateSwallowedScanner
{
    public static IReadOnlyList<TruncateSwallowedFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<TruncateSwallowedFinding> Findings { get; } = [];

        public override void ExplicitVisit(TryCatchStatement node)
        {
            if (!ContainsTruncate(node.TryStatements))
            {
                base.ExplicitVisit(node);
                return;
            }

            if (!ContainsPropagatingStatement(node.CatchStatements))
            {
                foreach (var truncate in FindTruncates(node.TryStatements))
                {
                    Findings.Add(new TruncateSwallowedFinding(sourcePath, truncate.StartLine, truncate.StartColumn));
                }
            }

            base.ExplicitVisit(node);
        }

        private static bool ContainsTruncate(TSqlFragment? fragment) => FindTruncates(fragment).Count != 0;

        private static List<TruncateTableStatement> FindTruncates(TSqlFragment? fragment)
        {
            if (fragment is null)
            {
                return [];
            }

            var collector = new NodeCollector<TruncateTableStatement>();
            fragment.Accept(collector);
            return collector.Nodes;
        }

        private static bool ContainsPropagatingStatement(TSqlFragment? fragment)
        {
            if (fragment is null)
            {
                return false;
            }

            var throwCollector = new NodeCollector<ThrowStatement>();
            fragment.Accept(throwCollector);
            if (throwCollector.Nodes.Count != 0)
            {
                return true;
            }

            var raiseErrorCollector = new NodeCollector<RaiseErrorStatement>();
            fragment.Accept(raiseErrorCollector);
            return raiseErrorCollector.Nodes.Count != 0;
        }

        private sealed class NodeCollector<T> : TSqlFragmentVisitor
            where T : TSqlFragment
        {
            public List<T> Nodes { get; } = [];

            public override void Visit(TSqlFragment node)
            {
                if (node is T match)
                {
                    Nodes.Add(match);
                }

                base.Visit(node);
            }
        }
    }
}
