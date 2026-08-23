using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class WaitForScanner
{
    public static IReadOnlyList<WaitForFinding> Scan(SqlParseResult parseResult)
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
        public List<WaitForFinding> Findings { get; } = [];

        private int _openTransactionDepth;

        public override void ExplicitVisit(TSqlBatch node)
        {
            _openTransactionDepth = 0;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BeginTransactionStatement node)
        {
            _openTransactionDepth++;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CommitTransactionStatement node)
        {
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(RollbackTransactionStatement node)
        {
            if (_openTransactionDepth > 0)
            {
                _openTransactionDepth--;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(WaitForStatement node)
        {
            if (node.WaitForOption is WaitForOption.Delay or WaitForOption.Time)
            {
                Findings.Add(new WaitForFinding(
                    sourcePath, node.StartLine, node.StartColumn, _openTransactionDepth > 0));
            }

            base.ExplicitVisit(node);
        }
    }
}
