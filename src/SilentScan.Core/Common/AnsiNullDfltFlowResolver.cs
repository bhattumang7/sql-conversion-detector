using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

internal static class AnsiNullDfltFlowResolver
{
    public static Dictionary<TSqlStatement, bool?> Resolve(TSqlFragment fragment)
    {
        var map = new Dictionary<TSqlStatement, bool?>(ReferenceEqualityComparer.Instance);
        if (fragment is not TSqlScript script)
        {
            return map;
        }

        var policy = new Policy(map);
        var state = default(FlowState);
        foreach (var batch in script.Batches)
        {
            state = ProcedureBodyFlowWalker.Walk(batch.Statements, state with { Depth = 0 }, policy);
        }

        return map;
    }

    private static StatementList? GetScopedStatementList(TSqlStatement statement) => statement switch
    {
        ProcedureStatementBodyBase p => p.StatementList,
        TriggerStatementBody t => t.StatementList,
        _ => null,
    };

    private readonly record struct FlowState(bool? Override, bool? RestoreOverride, int Depth);

    private sealed class Policy(Dictionary<TSqlStatement, bool?> map) : IStatementFlowPolicy<FlowState>
    {
        public bool IsDeclined(FlowState state) => false;

        public bool IsDone(FlowState state) => false;

        public FlowState PerStatement(TSqlStatement statement, FlowState state)
        {
            map[statement] = state.Override;

            if (GetScopedStatementList(statement) is { Statements: var nestedStatements })
            {
                ProcedureBodyFlowWalker.Walk(nestedStatements, default(FlowState), this);
            }

            if (statement is not PredicateSetStatement { Options: var options, IsOn: var isOn })
            {
                return state;
            }

            if ((options & SetOptions.AnsiNullDfltOn) != 0)
            {
                return state with { Override = isOn };
            }

            if ((options & SetOptions.AnsiNullDfltOff) != 0 && isOn)
            {
                return state with { Override = false };
            }

            return state;
        }

        public FlowState OnReturn(FlowState state, TSqlStatement statement) => state;

        public FlowState OnThrow(FlowState state) => state;

        public FlowState OnGoTo(FlowState state) => state;

        public FlowState CloneForBranch(FlowState state) =>
            state with { RestoreOverride = state.Override, Depth = state.Depth + 1 };

        public FlowState Merge(FlowState a, FlowState b)
        {
            var winner = a.Depth >= b.Depth ? a : b;
            return new FlowState(winner.RestoreOverride, winner.RestoreOverride, winner.Depth - 1);
        }
    }
}
