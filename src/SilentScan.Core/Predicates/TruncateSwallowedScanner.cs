using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class TruncateSwallowedScanner
{
    public static IReadOnlyList<TruncateSwallowedFinding> Scan(SqlParseResult parseResult)
    {
        var rule = new Rule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<TruncateSwallowedFinding> Findings { get; } = [];

        public void OnEnterTryCatchStatement(TryCatchStatement node, ModuleWalker walker)
        {
            if (!ContainsTruncate(node.TryStatements))
            {
                return;
            }

            if (!ContainsPropagatingStatement(node.CatchStatements))
            {
                foreach (var truncate in FindTruncates(node.TryStatements))
                {
                    Findings.Add(new TruncateSwallowedFinding(sourcePath, truncate.StartLine, truncate.StartColumn));
                }
            }
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

            public override void Visit(TSqlFragment fragment)
            {
                if (fragment is T match)
                {
                    Nodes.Add(match);
                }

                base.Visit(fragment);
            }
        }
    }
}
