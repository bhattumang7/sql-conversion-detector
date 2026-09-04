using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

internal readonly record struct SetOptionFlowState(bool IsOff, bool RestoreIsOff, int Depth);

internal abstract class SetOptionFlowPolicyBase : IStatementFlowPolicy<SetOptionFlowState>
{
    public bool IsDeclined(SetOptionFlowState state) => false;

    public bool IsDone(SetOptionFlowState state) => false;

    public abstract SetOptionFlowState PerStatement(TSqlStatement statement, SetOptionFlowState state);

    public SetOptionFlowState OnReturn(SetOptionFlowState state, TSqlStatement statement) => state;

    public SetOptionFlowState OnThrow(SetOptionFlowState state) => state;

    public SetOptionFlowState OnGoTo(SetOptionFlowState state) => state;

    public SetOptionFlowState CloneForBranch(SetOptionFlowState state) =>
        state with { RestoreIsOff = state.IsOff, Depth = state.Depth + 1 };

    public SetOptionFlowState Merge(SetOptionFlowState a, SetOptionFlowState b)
    {
        var winner = a.Depth >= b.Depth ? a : b;
        return new SetOptionFlowState(winner.RestoreIsOff, winner.RestoreIsOff, winner.Depth - 1);
    }
}
