using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class WaitForScanner
{
    public static IReadOnlyList<WaitForFinding> Scan(SqlParseResult parseResult)
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
        public List<WaitForFinding> Findings { get; } = [];

        private int _openTransactionDepth;

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => _openTransactionDepth = 0;

        public void OnEnterBeginTransactionStatement(BeginTransactionStatement node, ModuleWalker walker) => _openTransactionDepth++;

        public void OnEnterCommitTransactionStatement(CommitTransactionStatement node, ModuleWalker walker)
        {
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }
        }

        public void OnEnterRollbackTransactionStatement(RollbackTransactionStatement node, ModuleWalker walker)
        {
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }
        }

        public void OnEnterWaitForStatement(WaitForStatement node, ModuleWalker walker)
        {
            if (node.WaitForOption is WaitForOption.Delay or WaitForOption.Time)
            {
                Findings.Add(new WaitForFinding(
                    sourcePath, node.StartLine, node.StartColumn, _openTransactionDepth > 0));
            }
        }
    }
}
