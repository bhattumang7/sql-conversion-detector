using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

internal static class ConcatNullYieldsNullFlowResolver
{
    public static Dictionary<TSqlStatement, bool> Resolve(TSqlFragment fragment)
    {
        var map = new Dictionary<TSqlStatement, bool>(ReferenceEqualityComparer.Instance);
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

    private readonly record struct FlowState(bool IsOff, bool RestoreIsOff, int Depth);

    private sealed class Policy(Dictionary<TSqlStatement, bool> map) : IStatementFlowPolicy<FlowState>
    {
        private static StatementList? GetScopedStatementList(TSqlStatement statement) => statement switch
        {
            ProcedureStatementBodyBase p => p.StatementList,
            TriggerStatementBody t => t.StatementList,
            _ => null,
        };

        public bool IsDeclined(FlowState state) => false;

        public bool IsDone(FlowState state) => false;

        public FlowState PerStatement(TSqlStatement statement, FlowState state)
        {
            map[statement] = state.IsOff;

            if (GetScopedStatementList(statement) is { Statements: var nestedStatements })
            {
                ProcedureBodyFlowWalker.Walk(nestedStatements, default(FlowState), this);
            }

            if (statement is PredicateSetStatement { Options: var options, IsOn: var isOn } && (options & SetOptions.ConcatNullYieldsNull) != 0)
            {
                return state with { IsOff = !isOn };
            }

            return state;
        }

        public FlowState OnReturn(FlowState state, TSqlStatement statement) => state;

        public FlowState OnThrow(FlowState state) => state;

        public FlowState OnGoTo(FlowState state) => state;

        public FlowState CloneForBranch(FlowState state) =>
            state with { RestoreIsOff = state.IsOff, Depth = state.Depth + 1 };

        public FlowState Merge(FlowState a, FlowState b)
        {
            var winner = a.Depth >= b.Depth ? a : b;
            return new FlowState(winner.RestoreIsOff, winner.RestoreIsOff, winner.Depth - 1);
        }
    }
}
