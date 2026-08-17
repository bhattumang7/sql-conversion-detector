using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds": WAITFOR DELAY/WAITFOR TIME inside a routine or
/// batch. Fully syntax-only. Tracks an open-transaction depth (BEGIN TRAN increments, COMMIT/
/// ROLLBACK decrements) purely to flag the sharper "held inside a transaction" case on the finding
/// itself - a structural, not dataflow-complete, signal: a WAITFOR reachable only through a branch
/// where the transaction was already closed is not disambiguated (the same class of known,
/// documented imprecision <see cref="SelfReferencingDmlScanner"/> already accepts for its own
/// alias-reuse case) - depth merely reflects "how many BEGIN TRAN have been seen with no matching
/// COMMIT/ROLLBACK yet in this batch's own straight-line reading order," not real control-flow
/// analysis.
/// </summary>
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
