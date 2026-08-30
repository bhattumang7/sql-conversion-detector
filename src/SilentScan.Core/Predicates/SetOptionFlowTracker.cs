using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

internal static class SetOptionFlowTracker
{
    public static bool ComputeFinalOffState(TSqlFragment fragment, SetOptions flag)
    {
        if (fragment is not TSqlScript script)
        {
            return false;
        }

        var policy = new Policy(flag);
        var state = default(FlowState);
        foreach (var batch in script.Batches)
        {
            state = ProcedureBodyFlowWalker.Walk(batch.Statements, state with { Depth = 0 }, policy);
        }

        return state.IsOff;
    }

    private readonly record struct FlowState(bool IsOff, bool RestoreIsOff, int Depth);

    private sealed class Policy(SetOptions flag) : IStatementFlowPolicy<FlowState>
    {
        public bool IsDeclined(FlowState state) => false;

        public bool IsDone(FlowState state) => false;

        public FlowState PerStatement(TSqlStatement statement, FlowState state) =>
            statement is PredicateSetStatement { Options: var options, IsOn: var isOn } && (options & flag) != 0
                ? state with { IsOff = !isOn }
                : state;

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
